using System;
using System.Collections.Generic;

internal static class NativeInputPolicyTests
{
    static int checks;
    const long MouseMove = 0x0200;

    static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }

    static int Interferences(params long[] messages)
    {
        int count = 0;
        foreach (long message in messages)
            if (NativeMouseInput.IsInterferingMouseMessage(message)) count++;
        return count;
    }

    static void CheckSwitchingKeys()
    {
        var keys = new NativeMouseInput.BackgroundSwitchKeys();
        foreach (int alt in new[] { 0x12, 0xA4, 0xA5 })
            foreach (bool modifierFirst in new[] { false, true })
            {
                keys.Reset();
                Check(keys.Observe(0x0104, alt, false), "Alt down can begin switching: " + alt);
                Check(keys.Observe(0x0104, 0x09, true), "Alt+Tab press is a switching chord");
                if (modifierFirst) Check(keys.Observe(0x0105, alt, true), "Alt may release before Tab");
                Check(keys.Observe(0x0101, 0x09, !modifierFirst), "matching Tab release remains switching after focus/modifier changes");
                if (!modifierFirst) Check(keys.Observe(0x0105, alt, true), "Alt may release after Tab");
                Check(!keys.Observe(0x0101, 0x09, false), "duplicate Tab release is no longer exempt");
                Check(!keys.Observe(0x0100, 0x09, false), "later plain Tab remains direct page input");
            }

        foreach (int shift in new[] { 0x10, 0xA0, 0xA1 })
            foreach (bool modifierFirst in new[] { false, true })
            {
                keys.Reset();
                Check(keys.Observe(0x0104, 0xA4, false), "Alt starts reverse switching");
                Check(keys.Observe(0x0104, shift, true), "Shift under Alt can reverse app switching");
                Check(keys.Observe(0x0104, 0x09, true), "Alt+Shift+Tab press is permitted");
                if (modifierFirst) Check(keys.Observe(0x0105, 0xA4, true), "reverse-switch Alt can release first");
                Check(keys.Observe(0x0101, shift, !modifierFirst), "reverse-switch Shift release retains its chord");
                Check(keys.Observe(0x0101, 0x09, !modifierFirst), "reverse-switch Tab release retains its chord");
                if (!modifierFirst) Check(keys.Observe(0x0105, 0xA4, true), "reverse-switch Alt can release last");
                Check(!keys.Observe(0x0100, shift, false), "ordinary Shift without a switching chord remains guarded");
            }

        foreach (int windows in new[] { 0x5B, 0x5C })
            foreach (int key in new[] { 0x09, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39 })
                foreach (bool modifierFirst in new[] { false, true })
                {
                    keys.Reset();
                    Check(keys.Observe(0x0100, windows, false), "Windows key down may open another app: " + windows);
                    Check(keys.Observe(0x0100, key, false), "Windows app-switch chord is permitted: " + key);
                    Check(keys.Observe(0x0100, key, false), "repeated app-switch chord retains its modifier");
                    if (modifierFirst) Check(keys.Observe(0x0101, windows, false), "Windows key may release before switch key");
                    Check(keys.Observe(0x0101, key, false), "switch-key release is matched after another app or helper gains focus");
                    if (!modifierFirst) Check(keys.Observe(0x0101, windows, false), "Windows key may release after switch key");
                    Check(!keys.Observe(0x0101, key, false), "duplicate switch-key release is guarded");
                    Check(!keys.Observe(0x0100, key, false), "next ordinary Tab/number press is guarded");
                }

        keys.Reset();
        Check(keys.Observe(0x0100, 0x5B, false) && keys.Observe(0x0100, 0x5C, false), "both Windows modifiers can be held independently");
        Check(keys.Observe(0x0101, 0x5B, false) && keys.Observe(0x0100, 0x09, false), "releasing left Windows keeps right Windows switching active");
        Check(keys.Observe(0x0101, 0x5C, false) && keys.Observe(0x0101, 0x09, false), "matching release survives both Windows modifiers releasing");
        Check(!keys.Observe(0x0100, 0x09, false), "no Windows modifier remains stuck");

        foreach (bool left in new[] { false, true })
        {
            keys.Reset(left, !left);
            Check(keys.Observe(0x0100, 0x09, false), "Windows key already held at watch start permits app switching");
            Check(keys.Observe(0x0101, left ? 0x5B : 0x5C, false), "pre-held Windows modifier can release");
            Check(keys.Observe(0x0101, 0x09, false), "pre-held Windows chord release is matched");
        }

        keys.Reset();
        Check(keys.Observe(0x0104, 0x09, true), "Alt context permits a chord begun after the watch starts");
        Check(!keys.Observe(0x0100, 0x09, false), "Tab repeated after Alt releases becomes ordinary input");
        Check(!keys.Observe(0x0101, 0x09, false), "ordinary Tab repeat clears old switching release");
        Check(keys.Observe(0x0100, 0x5B, false) && keys.Observe(0x0100, 0x31, false), "prepare pending Windows switch before watch reset");
        Check(keys.Observe(0x0104, 0xA0, true) && keys.Observe(0x0104, 0x09, true), "prepare pending Alt switch before watch reset");
        keys.Reset();
        Check(!keys.Observe(0x0100, 0x09, false) && !keys.Observe(0x0101, 0x09, false), "watch reset clears Windows and Tab state");
        Check(!keys.Observe(0x0101, 0x31, false) && !keys.Observe(0x0101, 0xA0, false), "watch reset clears number and Shift releases");

        foreach (int key in new[] { 0x09, 0x10, 0x11, 0xA2, 0xA3, 0x30, 0x31, 0x41, 0x0D, 0x1B })
            foreach (long message in new long[] { 0x0100, 0x0101, 0x0104, 0x0105 })
            {
                keys.Reset();
                Check(!keys.Observe(message, key, false), "ordinary helper keyboard input remains guarded: " + key + "/" + message);
            }
        foreach (int key in new[] { 0x11, 0xA2, 0xA3, 0x41, 0x30, 0x0D, 0x1B, 0x77 })
        {
            keys.Reset();
            Check(!keys.Observe(0x0104, key, true), "Alt does not exempt content/control/stop keys: " + key);
        }
        foreach (bool windowsHeld in new[] { false, true })
            foreach (bool altHeld in new[] { false, true })
                foreach (long message in new long[] { 0x0100, 0x0104 })
                {
                    keys.Reset(windowsHeld, false);
                    Check(!keys.Observe(message, 0x77, altHeld) && NativeMouseInput.IsStopKeyDown(message, 0x77),
                        "F8 remains global independently of Windows/Alt switching state");
                }
        keys.Reset();
        Check(!keys.Observe(0x0400, 0x5B, false) && !keys.Observe(0x0100, 0x09, false), "unknown Windows message cannot latch switching state");
        Check(!keys.Observe(0x0101, 0x09, true) && !keys.Observe(0x0101, 0xA0, true), "unmatched pre-watch Tab/Shift releases remain guarded");
    }

    static int Main()
    {
        try
        {
            Check(!NativeMouseInput.IsInterferingMouseMessage(MouseMove), "pointer movement alone does not interrupt");
            var movement = new long[4096];
            for (int i = 0; i < movement.Length; i++) movement[i] = MouseMove;
            Check(Interferences(movement) == 0, "continuous pointer motion never accumulates interference");

            var interactions = new Dictionary<string, long> {
                { "left down", 0x0201 }, { "left up", 0x0202 }, { "left double click", 0x0203 },
                { "right down", 0x0204 }, { "right up", 0x0205 }, { "right double click", 0x0206 },
                { "middle down", 0x0207 }, { "middle up", 0x0208 }, { "middle double click", 0x0209 },
                { "vertical wheel", 0x020A }, { "extra button down", 0x020B }, { "extra button up", 0x020C },
                { "extra button double click", 0x020D }, { "horizontal wheel", 0x020E }
            };
            foreach (var interaction in interactions)
            {
                Check(NativeMouseInput.IsInterferingMouseMessage(interaction.Value), interaction.Key + " still interrupts");
                Check(Interferences(MouseMove, MouseMove, interaction.Value, MouseMove) == 1,
                    "surrounding pointer motion cannot hide " + interaction.Key);
            }
            Check(Interferences(0x0201, MouseMove, MouseMove, 0x0202) == 2, "dragging preserves both button boundaries as interference");
            Check(Interferences(0x020B, MouseMove, 0x020A, MouseMove, 0x020C) == 3, "mixed extra-button and scrolling input remains interference");
            Check(Interferences(MouseMove, 0x0201, 0x0202, MouseMove, 0x0203) == 3, "a click followed by double click is never reduced to plain movement");

            foreach (long message in new long[] { 0, -1, 0x01FF, 0x020F, 0x00A0, 0x0400, 0x100000200L, Int64.MinValue, Int64.MaxValue })
                Check(NativeMouseInput.IsInterferingMouseMessage(message), "unknown mouse message fails closed: " + message);

            bool onlyMoveExempt = true;
            // Adjacent and unsupported message IDs must not accidentally become
            // exempt when the policy is changed to handle other input later.
            for (long message = 0x0100; message <= 0x0300; message++)
                if (!NativeMouseInput.IsInterferingMouseMessage(message) && message != MouseMove) onlyMoveExempt = false;
            Check(onlyMoveExempt, "only the exact mouse-move message is exempt in the nearby message range");

            foreach (var interaction in interactions)
            {
                Check(NativeMouseInput.IsScopedMouseInterference(interaction.Value, false, false),
                    "foreground mode still sees outside " + interaction.Key);
                Check(NativeMouseInput.IsScopedMouseInterference(interaction.Value, true, true),
                    "background mode still sees own-window " + interaction.Key);
                Check(!NativeMouseInput.IsScopedMouseInterference(interaction.Value, true, false),
                    "background mode ignores other-app " + interaction.Key);
            }
            foreach (bool background in new[] { false, true })
                foreach (bool targetsOwner in new[] { false, true })
                    Check(!NativeMouseInput.IsScopedMouseInterference(MouseMove, background, targetsOwner),
                        "pointer motion is harmless in mode=" + background + " owner=" + targetsOwner);
            Check(!NativeMouseInput.IsScopedMouseInterference(0x0201, true, false),
                "clicking another app to switch away is not owner input");
            Check(NativeMouseInput.IsScopedMouseInterference(0x0201, true, true),
                "clicking into the background helper interrupts before foreground changes");
            Check(NativeMouseInput.IsScopedMouseInterference(0x0400, true, true),
                "unknown owner mouse message still fails closed in background mode");
            Check(NativeMouseInput.IsScopedKeyboardInterference(false, false), "foreground mode retains global typing interference");
            Check(NativeMouseInput.IsScopedKeyboardInterference(false, true), "foreground mode retains own typing interference");
            Check(!NativeMouseInput.IsScopedKeyboardInterference(true, false), "background mode permits other-app typing");
            Check(NativeMouseInput.IsScopedKeyboardInterference(true, true), "background mode stops on typing in the helper");
            foreach (bool background in new[] { false, true })
                foreach (bool ownerForeground in new[] { false, true })
                {
                    // F8 classification is independent of scoped interference;
                    // the hook must retain the stop even when input is ignored.
                    Check(NativeMouseInput.IsStopKeyDown(0x0100, 0x77),
                        "F8 stops for mode=" + background + " ownerForeground=" + ownerForeground);
                    Check(NativeMouseInput.IsStopKeyDown(0x0104, 0x77),
                        "system F8 stops for mode=" + background + " ownerForeground=" + ownerForeground);
                }
            Check(!NativeMouseInput.IsStopKeyDown(0x0101, 0x77), "F8 release is not another stop press");
            Check(!NativeMouseInput.IsStopKeyDown(0x0105, 0x77), "system F8 release is not another stop press");
            Check(!NativeMouseInput.IsStopKeyDown(0x0100, 0x78), "other function key does not request stop");
            Check(!NativeMouseInput.IsStopKeyDown(0x0400, 0x77), "unknown keyboard message is not an F8 down");

            CheckSwitchingKeys();

            foreach (long wheel in new long[] { 0x020A, 0x020E })
            {
                Check(NativeMouseInput.IsScopedMouseInterference(wheel, true, false, true), "foreground helper may receive wheel even when cursor is outside");
                Check(!NativeMouseInput.IsScopedMouseInterference(wheel, true, false, false), "wheel in another foreground app remains independent");
                Check(NativeMouseInput.IsScopedMouseInterference(wheel, true, true, false), "inactive scrolling into helper still interrupts");
            }
            Check(!NativeMouseInput.IsScopedMouseInterference(0x0201, true, false, true), "outside click can still switch away from foreground helper");
            Console.WriteLine("PASS " + checks + " native input policy checks (pure classification; no hooks, native input or network).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
