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

            foreach (int altKey in new[] { 0x12, 0xA4, 0xA5 })
            {
                Check(NativeMouseInput.IsBackgroundSwitchKey(altKey, false), "Alt press may begin switching: " + altKey);
                Check(NativeMouseInput.IsBackgroundSwitchKey(altKey, true), "Alt release remains harmless: " + altKey);
            }
            Check(NativeMouseInput.IsBackgroundSwitchKey(0x09, true), "Alt+Tab can switch away and back without pausing");
            Check(!NativeMouseInput.IsBackgroundSwitchKey(0x09, false), "plain Tab or Ctrl+Tab still interferes");
            Check(!NativeMouseInput.IsBackgroundSwitchKey(0x11, true), "Control is not exempted by holding Alt");
            Check(!NativeMouseInput.IsBackgroundSwitchKey(0x41, true), "Alt+letter still interferes with the helper");
            Check(!NativeMouseInput.IsBackgroundSwitchKey(0x77, true) && NativeMouseInput.IsStopKeyDown(0x0104, 0x77),
                "Alt+F8 remains an immediate global stop");
            Check(!NativeMouseInput.IsBackgroundSwitchKey(0x09, false),
                "Tab released after Alt was released fails closed if the helper regained focus");

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
