using System.Windows;

namespace PetGPT.Services;

public static class WindowPositionService
{
    public static void PutPetAtDefault(Window pet)
    {
        var work = SystemParameters.WorkArea;
        pet.Left = work.Right - pet.Width - 24;
        pet.Top = work.Bottom - pet.Height - 16;
    }

    public static void PositionBubble(Window bubble, Window pet)
    {
        var work = SystemParameters.WorkArea;
        const double gap = 10;

        var desiredLeft = pet.Left + (pet.Width - bubble.Width) / 2;
        var desiredTop = pet.Top - bubble.Height - gap;

        var maxLeft = Math.Max(work.Left + 8, work.Right - bubble.Width - 8);
        var left = Math.Clamp(desiredLeft, work.Left + 8, maxLeft);

        var top = desiredTop;
        if (top < work.Top + 8)
            top = pet.Top + pet.Height + gap;

        var maxTop = Math.Max(work.Top + 8, work.Bottom - bubble.Height - 8);
        top = Math.Clamp(top, work.Top + 8, maxTop);

        bubble.Left = left;
        bubble.Top = top;
    }
}
