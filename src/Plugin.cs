using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using InControl;
using UnityEngine;
using GlobalEnums;

namespace DiveBind
{
    // Tiny side-mod: bind a controller button to a reliable FORWARD DIVE ATTACK.
    // The game's down attack (a directional DownSpike for crests that have one) dives in the FACING direction, and
    // facing is set from the HELD stick (HeroController.TrySetCorrectFacing reads move_input) — so holding back or
    // neutral makes you dive backwards. This mod triggers the down attack but forces facing to your CURRENT MOTION
    // direction, so the dive always goes the way you're travelling. F4 opens a bind menu. Default: Left Bumper (L1),
    // only while airborne — and while diving, L1 does NOT pull out the quick-map until released and pressed again.
    [BepInPlugin(Guid, "Silksong Dive Bind", "0.4.1")]
    public sealed class DivePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.will.silksong.divebind";
        internal static ManualLogSource Log;

        internal static ConfigEntry<InputControlType> CfgControl;
        internal static ConfigEntry<bool> CfgOnlyInAir;
        internal static ConfigEntry<bool> CfgSuppressMap;
        internal static ConfigEntry<bool> CfgFailsafe;
        internal static ConfigEntry<KeyCode> CfgMenuKey;

        // Stuck-dive failsafe state: armed for a few seconds after each mod dive.
        private float _watchUntil;
        private float _wedgeSince = -1f;

        // When the dive control also opens the quick-map (e.g. L1), we UNBIND it from the QuickMap action for the
        // duration of the dive-hold, so a held dive doesn't pull out the map on landing. Restored on release.
        internal static bool MapSuppressed;
        private static InputControlType _removedFromQuickMap;

        // Set true only during our synthetic attack so the TrySetCorrectFacing patch forces facing to _wantRight.
        internal static bool ForceDive;
        internal static bool WantRight;

        private static MethodInfo _attackMethod;   // private HeroController.Attack(AttackDirection)
        private bool _menu;
        private bool _rebind;
        private Rect _rect = new Rect(60, 60, 340, 210);
        private float _lastDiveAt;
        private string _status = "";

        // Controls offered for rebind capture (pad controls incl. triggers).
        private static readonly InputControlType[] Bindable =
        {
            InputControlType.RightTrigger, InputControlType.LeftTrigger,
            InputControlType.RightBumper, InputControlType.LeftBumper,
            InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
            InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
            InputControlType.LeftStickButton, InputControlType.RightStickButton,
        };

        private void Awake()
        {
            Log = Logger;
            CfgControl = Config.Bind("Dive", "Control", InputControlType.LeftBumper,
                "Controller control that triggers the forward dive attack. Default LeftBumper (L1). Rebind in the F4 menu.");
            CfgOnlyInAir = Config.Bind("Dive", "OnlyInAir", true,
                "Only fire the dive while airborne (so it doesn't hijack a grounded button press).");
            CfgSuppressMap = Config.Bind("Dive", "SuppressMapOnDive", true,
                "If the dive control also opens the quick-map (e.g. L1), don't pull out the map while diving — suppressed until you release and press the control again.");
            CfgFailsafe = Config.Bind("Dive", "StuckFailsafe", true,
                "If a dive ever leaves Hornet frozen mid-air with no control (gravity off, not moving), automatically restore control after ~1.5s.");
            CfgMenuKey = Config.Bind("Dive", "MenuKey", KeyCode.F4, "Key that opens the Dive Bind menu.");

            _attackMethod = AccessTools.Method(typeof(HeroController), "Attack", new[] { typeof(AttackDirection) });
            if (_attackMethod == null) Log.LogError("Could not find HeroController.Attack(AttackDirection) — dive won't fire.");

            new Harmony(Guid).PatchAll(typeof(DivePlugin).Assembly);
            Log.LogInfo("DiveBind v0.4.1 ready. F4 for menu. Default: L1, airborne only; map suppressed while diving.");
        }

        private void Update()
        {
            try
            {
                if (CfgMenuKey != null && Input.GetKeyDown(CfgMenuKey.Value)) _menu = !_menu;

                // Release the quick-map suppression once the dive control is let go (one hold = one suppression).
                if (MapSuppressed)
                {
                    var d0 = InputManager.ActiveDevice;
                    var c0 = d0 != null ? d0.GetControl(_removedFromQuickMap) : null;
                    if (c0 == null || !c0.IsPressed) RestoreQuickMap();
                }

                if (_rebind) { TryCaptureRebind(); return; }

                var hero = HeroController.instance;
                var gm = GameManager._instance;
                if (hero == null || gm == null || gm.GameState != GameState.PLAYING) return;

                WatchdogTick(hero, gm);

                var dev = InputManager.ActiveDevice;
                if (dev == null) return;
                var ctrl = dev.GetControl(CfgControl.Value);
                if (ctrl == null || !ctrl.WasPressed) return;

                bool airborne = !hero.cState.onGround;
                if (CfgOnlyInAir.Value && !airborne) { _status = "on ground — ignored"; return; }
                if (Time.unscaledTime - _lastDiveAt < 0.18f) return;               // debounce

                // Gate EXACTLY like the game's own attack input (acceptingInput && CanAttack()) — that covers
                // attack cooldown, attacking, dead/hazard states, relinquished control, recoil (no_input),
                // hard/dash landings, and UI input blocks. On top of that, the downspike-specific states:
                // vanilla only checks these when the Down button is held, and a DownSpike-crest dive never
                // sets cState.attacking, so without these an L1 press MID-DIVE or mid-pogo re-entered
                // Attack() and could cancel the dive after RelinquishControl — control was never returned
                // and Hornet froze. Never re-enter while any downspike state is live, or during a transition.
                if (!hero.acceptingInput || !hero.CanAttack()) return;
                if (hero.cState.dashing || hero.cState.downSpiking || hero.cState.downSpikeAntic
                    || hero.cState.downSpikeBouncing || hero.cState.downSpikeRecovery) return;
                if (hero.transitionState != HeroTransitionState.WAITING_TO_TRANSITION) return;   // mid scene transition

                DoDive(hero);
            }
            catch (Exception e) { Log.LogWarning("update: " + e.Message); }
        }

        private void DoDive(HeroController hero)
        {
            // Direction from current horizontal motion; fall back to held input, then current facing.
            float vx = hero.Body != null ? hero.Body.linearVelocity.x : 0f;
            bool wantRight;
            if (Mathf.Abs(vx) > 0.1f) wantRight = vx > 0f;
            else if (Mathf.Abs(hero.move_input) > 0.1f) wantRight = hero.move_input > 0f;
            else wantRight = hero.cState.facingRight;

            WantRight = wantRight;
            ForceDive = true;
            try { _attackMethod?.Invoke(hero, new object[] { AttackDirection.downward }); }
            finally { ForceDive = false; }

            // If the dive control also opens the quick-map, suppress that for the rest of this hold.
            if (CfgSuppressMap.Value) SuppressQuickMap();

            _lastDiveAt = Time.unscaledTime;
            _watchUntil = Time.unscaledTime + 3f;   // arm the stuck failsafe for this dive
            _wedgeSince = -1f;
            _status = "DIVE " + (wantRight ? "→ right" : "← left") + " (vx=" + vx.ToString("0.0") + ")";
        }

        // Failsafe: for a few seconds after a mod dive, look for the wedge signature — mid-air, control
        // relinquished, gravity switched off, zero velocity, and NO attack/downspike state actually live
        // (the dive machinery relinquished control and then lost track of us). If that persists 1.5s,
        // hand control back. Deliberately narrow so real cutscenes/grabs are never touched.
        private void WatchdogTick(HeroController hero, GameManager gm)
        {
            if (!CfgFailsafe.Value || _watchUntil <= 0f) return;
            if (Time.unscaledTime > _watchUntil) { _watchUntil = 0f; _wedgeSince = -1f; return; }

            bool wedged = hero.controlReqlinquished
                && !hero.cState.onGround
                && !hero.cState.attacking && !hero.cState.downSpiking && !hero.cState.downSpikeAntic
                && !hero.cState.hazardDeath && !hero.cState.hazardRespawning && !hero.cState.dead
                && hero.transitionState == HeroTransitionState.WAITING_TO_TRANSITION
                && hero.Body != null && hero.Body.gravityScale == 0f
                && hero.Body.linearVelocity.sqrMagnitude < 0.01f;

            if (!wedged) { _wedgeSince = -1f; return; }
            if (_wedgeSince < 0f) { _wedgeSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _wedgeSince < 1.5f) return;

            _watchUntil = 0f; _wedgeSince = -1f;
            hero.ResetGravity();
            hero.RegainControl();
            _status = "stuck-dive failsafe fired — control restored";
            Log.LogWarning("[divebind] stuck-dive failsafe: hero was frozen post-dive (control relinquished, gravity off); restored control.");
        }

        // Remove the dive control from the QuickMap action so a held dive can't pull out the map. We still read the
        // physical control via InControl, so the dive itself is unaffected. No-op if the dive control isn't bound to
        // QuickMap (e.g. R2) — then nothing is removed and MapSuppressed stays false.
        private static void SuppressQuickMap()
        {
            if (MapSuppressed) return;
            try
            {
                var qm = InputHandler.Instance != null ? InputHandler.Instance.inputActions?.QuickMap : null;
                if (qm == null) return;
                for (int i = qm.Bindings.Count - 1; i >= 0; i--)
                {
                    if (qm.Bindings[i] is DeviceBindingSource dbs && dbs.Control == CfgControl.Value)
                    {
                        qm.RemoveBinding(qm.Bindings[i]);
                        _removedFromQuickMap = CfgControl.Value;
                        MapSuppressed = true;
                        Log.LogInfo("suppressed quick-map on " + _removedFromQuickMap + " for the dive hold.");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning("suppress map: " + e.Message); }
        }

        private static void RestoreQuickMap()
        {
            if (!MapSuppressed) return;
            try
            {
                var qm = InputHandler.Instance != null ? InputHandler.Instance.inputActions?.QuickMap : null;
                if (qm != null)
                {
                    bool has = false;
                    foreach (var b in qm.Bindings)
                        if (b is DeviceBindingSource dbs && dbs.Control == _removedFromQuickMap) { has = true; break; }
                    if (!has) qm.AddBinding(new DeviceBindingSource(_removedFromQuickMap));
                }
                Log.LogInfo("restored quick-map on " + _removedFromQuickMap + ".");
            }
            catch (Exception e) { Log.LogWarning("restore map: " + e.Message); }
            MapSuppressed = false;
        }

        private void OnDisable() => RestoreQuickMap();   // never leave the quick-map unbound if the mod unloads

        private void TryCaptureRebind()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { _rebind = false; return; }
            var dev = InputManager.ActiveDevice;
            if (dev == null) return;
            foreach (var t in Bindable)
            {
                var c = dev.GetControl(t);
                if (c != null && c.WasPressed) { CfgControl.Value = t; _rebind = false; Log.LogInfo("Dive bound to " + t); return; }
            }
        }

        private void OnGUI()
        {
            if (!_menu) return;
            _rect = GUILayout.Window(0x0D1E, _rect, DrawMenu, "Dive Bind");
        }

        private void DrawMenu(int id)
        {
            GUILayout.Space(4);
            GUILayout.Label("Forward dive attack — fires in your direction of motion.");
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Button:", GUILayout.Width(60));
            GUILayout.Label(_rebind ? "press a controller button… (Esc cancels)" : CfgControl.Value.ToString());
            GUILayout.EndHorizontal();
            if (GUILayout.Button(_rebind ? "listening…" : "Rebind")) _rebind = !_rebind;

            CfgOnlyInAir.Value = GUILayout.Toggle(CfgOnlyInAir.Value, " Only while airborne");
            CfgSuppressMap.Value = GUILayout.Toggle(CfgSuppressMap.Value, " Don't open map while diving (L1)");

            GUILayout.Space(6);
            var hero = HeroController.instance;
            string air = hero != null ? (hero.cState.onGround ? "grounded" : "airborne") : "no hero";
            GUILayout.Label("Hero: " + air);
            GUILayout.Label("Last: " + _status);

            GUILayout.Space(6);
            if (GUILayout.Button("Close")) _menu = false;
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }
    }

    // Force facing to the motion direction during our synthetic dive. HeroController.Attack() calls
    // TrySetCorrectFacing(force:true) which normally re-faces from the held stick (move_input); we override it so
    // the DownSpike's facing-derived horizontal direction dives the way the player is actually moving.
    [HarmonyPatch(typeof(HeroController), "TrySetCorrectFacing")]
    internal static class Patch_TrySetCorrectFacing
    {
        private static bool Prefix(HeroController __instance, ref bool __result)
        {
            if (!DivePlugin.ForceDive) return true;   // normal behaviour otherwise
            try
            {
                if (__instance.cState.facingRight != DivePlugin.WantRight)
                {
                    __instance.FlipSprite();
                    __result = true;
                }
                else __result = false;
            }
            catch { __result = false; }
            return false;   // skip the original stick-based facing
        }
    }

    // Race-proof backstop: if a quick-map open somehow gets queued from the dive-press frame before we unbind the
    // control, block the actual open here while suppression is active. (Normally the unbind stops it earlier and this
    // never fires — it only guards the same-frame race, and only during a dive-hold.)
    [HarmonyPatch(typeof(GameMap), "TryOpenQuickMap")]
    internal static class Patch_TryOpenQuickMap
    {
        private static bool Prefix(ref string displayName, ref bool __result)
        {
            if (!DivePlugin.MapSuppressed) return true;
            displayName = string.Empty;
            __result = false;
            return false;   // skip the open
        }
    }
}
