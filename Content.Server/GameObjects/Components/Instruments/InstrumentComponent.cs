﻿#nullable enable
using System;
using System.Linq;
using Content.Server.GameObjects.Components.Mobs;
using Content.Server.GameObjects.EntitySystems;
using Content.Server.Utility;
using Content.Shared.GameObjects.Components.Instruments;
using Content.Shared.GameObjects.EntitySystems;
using Content.Shared.Interfaces;
using Content.Shared.Interfaces.GameObjects.Components;
using Robust.Server.GameObjects;
using Robust.Server.GameObjects.Components.UserInterface;
using Robust.Server.Interfaces.GameObjects;
using Robust.Server.Interfaces.Player;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.GameObjects.Systems;
using Robust.Shared.Interfaces.Network;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.IoC;
using Robust.Shared.Players;
using Robust.Shared.Serialization;
using Robust.Shared.ViewVariables;

namespace Content.Server.GameObjects.Components.Instruments
{

    [RegisterComponent]
    [ComponentReference(typeof(IActivate))]
    public class InstrumentComponent
        : SharedInstrumentComponent,
            IDropped,
            IHandSelected,
            IHandDeselected,
            IActivate,
            IUse,
            IThrown
    {
        [Dependency] private readonly IGameTiming _gameTiming = default!;

        private static readonly TimeSpan OneSecAgo = TimeSpan.FromSeconds(-1);

        /// <summary>
        ///     The client channel currently playing the instrument, or null if there's none.
        /// </summary>
        [ViewVariables]
        private IPlayerSession? _instrumentPlayer;

        private bool _handheld;

        [ViewVariables]
        private bool _playing = false;

        [ViewVariables]
        private float _timer = 0f;

        [ViewVariables(VVAccess.ReadOnly)]
        private TimeSpan _lastMeasured = TimeSpan.MinValue;

        [ViewVariables]
        private int _batchesDropped = 0;

        [ViewVariables]
        private int _laggedBatches = 0;

        [ViewVariables]
        private uint _lastSequencerTick = 0;

        [ViewVariables]
        private int _midiEventCount = 0;

        private byte _instrumentProgram;
        private byte _instrumentBank;
        private bool _allowPercussion;
        private bool _allowProgramChange;

        [ViewVariables(VVAccess.ReadWrite)]
        public override byte InstrumentProgram { get => _instrumentProgram;
            set
            {
                _instrumentProgram = value;
                Dirty();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public override byte InstrumentBank { get => _instrumentBank;
            set
            {
                _instrumentBank = value;
                Dirty();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public override bool AllowPercussion { get => _allowPercussion;
            set
            {
                _allowPercussion = value;
                Dirty();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public override bool AllowProgramChange { get => _allowProgramChange;
            set
            {
                _allowProgramChange = value;
                Dirty();
            }
        }

        /// <summary>
        ///     Whether the instrument is an item which can be held or not.
        /// </summary>
        [ViewVariables]
        public bool Handheld => _handheld;

        /// <summary>
        ///     Whether the instrument is currently playing or not.
        /// </summary>
        [ViewVariables]
        public bool Playing
        {
            get => _playing;
            set
            {
                _playing = value;
                Dirty();
            }
        }

        public IPlayerSession? InstrumentPlayer
        {
            get => _instrumentPlayer;
            private set
            {
                Playing = false;

                if (_instrumentPlayer != null)
                    _instrumentPlayer.PlayerStatusChanged -= OnPlayerStatusChanged;

                _instrumentPlayer = value;

                if (value != null)
                    _instrumentPlayer!.PlayerStatusChanged += OnPlayerStatusChanged;
            }
        }

        [ViewVariables] private BoundUserInterface? UserInterface => Owner.GetUIOrNull(InstrumentUiKey.Key);

        private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
        {
            if (e.Session != _instrumentPlayer || e.NewStatus != SessionStatus.Disconnected) return;
            InstrumentPlayer = null;
            Clean();
        }

        public override void Initialize()
        {
            base.Initialize();

            if (UserInterface != null)
            {
                UserInterface.OnClosed += UserInterfaceOnClosed;
            }
        }

        public override void ExposeData(ObjectSerializer serializer)
        {
            base.ExposeData(serializer);
            serializer.DataField(ref _handheld, "handheld", false);
            serializer.DataField(ref _instrumentProgram, "program", (byte) 1);
            serializer.DataField(ref _instrumentBank, "bank", (byte) 0);
            serializer.DataField(ref _allowPercussion, "allowPercussion", false);
            serializer.DataField(ref _allowProgramChange, "allowProgramChange", false);
        }

        public override ComponentState GetComponentState()
        {
            return new InstrumentState(Playing, InstrumentProgram, InstrumentBank, AllowPercussion, AllowProgramChange, _lastSequencerTick);
        }

        public override void HandleNetworkMessage(ComponentMessage message, INetChannel channel, ICommonSession? session = null)
        {
            base.HandleNetworkMessage(message, channel, session);

            switch (message)
            {
                case InstrumentMidiEventMessage midiEventMsg:
                    if (!Playing || session != _instrumentPlayer || InstrumentPlayer == null) return;

                    var send = true;

                    var minTick = midiEventMsg.MidiEvent.Min(x => x.Tick);
                    if (_lastSequencerTick > minTick)
                    {
                        var now = _gameTiming.RealTime;
                        var oneSecAGo = now.Add(OneSecAgo);
                        if (_lastMeasured < oneSecAGo)
                        {
                            _lastMeasured = now;
                            _laggedBatches = 0;
                            _batchesDropped = 0;
                        }

                        _laggedBatches++;
                        switch (_laggedBatches)
                        {
                            case (int) (MaxMidiLaggedBatches * (1 / 3d)) + 1:
                                Owner.PopupMessage(InstrumentPlayer.AttachedEntity,
                                    "Your fingers are beginning to a cramp a little!");
                                break;
                            case (int) (MaxMidiLaggedBatches * (2 / 3d)) + 1:
                                Owner.PopupMessage(InstrumentPlayer.AttachedEntity,
                                    "Your fingers are seriously cramping up!");
                                break;
                        }

                        if (_laggedBatches > MaxMidiLaggedBatches)
                        {
                            send = false;
                        }
                    }

                    if (++_midiEventCount > MaxMidiEventsPerSecond
                        || midiEventMsg.MidiEvent.Length > MaxMidiEventsPerBatch)
                    {
                        var now = _gameTiming.RealTime;
                        var oneSecAGo = now.Add(OneSecAgo);
                        if (_lastMeasured < oneSecAGo)
                        {
                            _lastMeasured = now;
                            _laggedBatches = 0;
                            _batchesDropped = 0;
                        }

                        _batchesDropped++;

                        send = false;
                    }

                    if (send)
                    {
                        SendNetworkMessage(midiEventMsg);
                    }

                    var maxTick = midiEventMsg.MidiEvent.Max(x => x.Tick);
                    _lastSequencerTick = Math.Max(maxTick, minTick + 1);
                    break;
                case InstrumentStartMidiMessage startMidi:
                    if (session != _instrumentPlayer)
                        break;
                    Playing = true;
                    break;
                case InstrumentStopMidiMessage stopMidi:
                    if (session != _instrumentPlayer)
                        break;
                    Playing = false;
                    Clean();
                    break;
            }
        }

        private void Clean()
        {
            Playing = false;
            _lastSequencerTick = 0;
            _batchesDropped = 0;
            _laggedBatches = 0;
        }

        public void Dropped(DroppedEventArgs eventArgs)
        {
            Clean();
            SendNetworkMessage(new InstrumentStopMidiMessage());
            InstrumentPlayer = null;
            UserInterface?.CloseAll();
        }

        public void Thrown(ThrownEventArgs eventArgs)
        {
            Clean();
            SendNetworkMessage(new InstrumentStopMidiMessage());
            InstrumentPlayer = null;
            UserInterface?.CloseAll();
        }

        public void HandSelected(HandSelectedEventArgs eventArgs)
        {
            if (eventArgs.User == null || !eventArgs.User.TryGetComponent(out BasicActorComponent? actor))
                return;

            var session = actor.playerSession;

            if (session.Status != SessionStatus.InGame) return;

            InstrumentPlayer = session;
        }

        public void HandDeselected(HandDeselectedEventArgs eventArgs)
        {
            Clean();
            SendNetworkMessage(new InstrumentStopMidiMessage());
            UserInterface?.CloseAll();
        }

        public void Activate(ActivateEventArgs eventArgs)
        {
            if (Handheld || !eventArgs.User.TryGetComponent(out IActorComponent? actor)) return;

            if (InstrumentPlayer != null) return;

            InstrumentPlayer = actor.playerSession;
            OpenUserInterface(actor.playerSession);
        }

        public bool UseEntity(UseEntityEventArgs eventArgs)
        {
            if (!eventArgs.User.TryGetComponent(out IActorComponent? actor)) return false;

            if (InstrumentPlayer == actor.playerSession)
            {
                OpenUserInterface(actor.playerSession);
            }

            return false;
        }

        private void UserInterfaceOnClosed(IPlayerSession player)
        {
            if (Handheld || player != InstrumentPlayer) return;

            Clean();
            InstrumentPlayer = null;
            SendNetworkMessage(new InstrumentStopMidiMessage());
        }

        private void OpenUserInterface(IPlayerSession session)
        {
            UserInterface?.Toggle(session);
        }

        public override void Update(float delta)
        {
            base.Update(delta);

            if (_instrumentPlayer != null && !ActionBlockerSystem.CanInteract(_instrumentPlayer.AttachedEntity))
            {
                InstrumentPlayer = null;
                Clean();
                UserInterface?.CloseAll();
            }

            if ((_batchesDropped >= MaxMidiBatchDropped
                    || _laggedBatches >= MaxMidiLaggedBatches)
                && InstrumentPlayer != null)
            {
                var mob = InstrumentPlayer.AttachedEntity;

                SendNetworkMessage(new InstrumentStopMidiMessage());
                Playing = false;

                UserInterface?.CloseAll();

                if (mob != null && mob.TryGetComponent(out StunnableComponent? stun))
                {
                    stun.Stun(1);
                    Clean();
                }
                else
                {
                    EntitySystem.Get<StandingStateSystem>().DropAllItemsInHands(mob, false);
                }

                InstrumentPlayer = null;

                Owner.PopupMessage(mob, "Your fingers cramp up from playing!");
            }

            _timer += delta;
            if (_timer < 1) return;

            _timer = 0f;
            _midiEventCount = 0;
        }

    }

}
