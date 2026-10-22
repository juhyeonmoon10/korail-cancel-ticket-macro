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

            Console.WriteLine("PASS " + checks + " native input policy checks (pure classification; no hooks, native input or network).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
