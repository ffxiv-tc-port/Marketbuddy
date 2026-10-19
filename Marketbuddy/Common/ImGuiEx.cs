using System;
using Dalamud.Bindings.ImGui;

namespace Marketbuddy.Common;

public static class ImGuiEx
{
    internal static string GetClipboardText()
    {
        try
        {
            return ImGui.GetClipboardText() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 寬 <paramref name="nextWidth"/> 的下一個項目接在上一個項目右邊還放得下才 SameLine（回 true），
    /// 否則讓它從下一行開始（回 false）。放得下時與直接 SameLine 相同；放不下時直接 SameLine 會把它推出視窗右邊。
    /// </summary>
    internal static bool SameLineIfFits(float nextWidth)
    {
        var right = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + nextWidth;
        if (right <= ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X)
        {
            ImGui.SameLine();
            return true;
        }

        return false;
    }

    /// <summary>不給大小的 <c>ImGui.Button</c> 會畫多寬。</summary>
    internal static float ButtonWidth(string label)
        => ImGui.CalcTextSize(label, true).X + (ImGui.GetStyle().FramePadding.X * 2);

    /// <summary>先 <c>SetNextItemWidth(itemWidth)</c> 再畫、帶標籤的輸入框（標籤畫在框的右邊）總共多寬。</summary>
    internal static float LabeledItemWidth(float itemWidth, string label)
    {
        var labelWidth = ImGui.CalcTextSize(label, true).X;
        return labelWidth > 0 ? itemWidth + ImGui.GetStyle().ItemInnerSpacing.X + labelWidth : itemWidth;
    }
}