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

    private static readonly ButtonSettle MarketActionSettle = new();

    /// <summary>
    /// 會觸發市場動作（上架、重掛、下架、賣商店，含兩段式的武裝與確認）的按鈕：
    /// 位置最近 <see cref="ButtonSettle.SettleMs"/> 內變過或剛出現就停用、不接受點擊。純防呆，不改任何觸發條件。
    /// <paramref name="key"/> 是固定的追蹤鍵，按鈕文字（倒數秒數等）變了不算移位。停止／取消類按鈕刻意不走這裡：面板一直在動時也要按得到。
    /// </summary>
    internal static bool MarketActionButton(string label, string key)
    {
        var settled = MarketActionSettle.Observe(key, ImGui.GetCursorScreenPos(), DateTime.UtcNow);
        if (!settled)
            ImGui.BeginDisabled();
        var clicked = ImGui.Button(label);
        if (!settled)
            ImGui.EndDisabled();
        return clicked && settled;
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