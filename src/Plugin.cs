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
    // direction, so the dive always goes the way you're travelling. F4 opens a bind menu. Default: Right Trigger (R2),
    // only while airborne.
    [BepInPlugin(Guid, "Silksong Dive Bind", "0.1.0")]
    public sealed class DivePlugin : BaseUnityPlugin
    {
        public const string Guid = "com.will.silksong.divebind";
        internal static ManualLogSource Log;

        internal static ConfigEntry<InputControlType> CfgControl;
        internal static ConfigEntry<bool> CfgOnlyInAir;
        internal static ConfigEntry<KeyCode> CfgMenuKey;

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
            CfgControl = Config.Bind("Dive", "Control", InputControlType.RightTrigger,
                "Controller control that triggers the forward dive attack. Default RightTrigger (R2). Rebind in the F4 menu.");
            CfgOnlyInAir = Config.Bind("Dive", "OnlyInAir", true,
                "Only fire the dive while airborne (so it doesn't hijack a grounded button press).");
            CfgMenuKey = Config.Bind("Dive", "MenuKey", KeyCode.F4, "Key that opens the Dive Bind menu.");

            _attackMethod = AccessTools.Method(typeof(HeroController), "Attack", new[] { typeof(AttackDirection) });
            if (_attackMethod == null) Log.LogError("Could not find HeroController.Attack(AttackDirection) — dive won't fire.");

            new Harmony(Guid).PatchAll(typeof(DivePlugin).Assembly);
            Log.LogInfo("DiveBind ready. F4 for menu. Default: R2, airborne only.");
        }

        private void Update()
        {
            try
            {
                if (CfgMenuKey != null && Input.GetKeyDown(CfgMenuKey.Value)) _menu = !_menu;

                if (_rebind) { TryCaptureRebind(); return; }

                var hero = HeroController.instance;
                var gm = GameManager._instance;
                if (hero == null || gm == null || gm.GameState != GameState.PLAYING) return;

                var dev = InputManager.ActiveDevice;
                if (dev == null) return;
                var ctrl = dev.GetControl(CfgControl.Value);
                if (ctrl == null || !ctrl.WasPressed) return;

                bool airborne = !hero.cState.onGround;
                if (CfgOnlyInAir.Value && !airborne) { _status = "on ground — ignored"; return; }
                if (Time.unscaledTime - _lastDiveAt < 0.18f) return;               // debounce
                if (hero.cState.attacking || hero.cState.dashing || hero.cState.hazardDeath
                    || hero.cState.hazardRespawning || hero.controlReqlinquished) return;

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

            _lastDiveAt = Time.unscaledTime;
            _status = "DIVE " + (wantRight ? "→ right" : "← left") + " (vx=" + vx.ToString("0.0") + ")";
        }

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
}
