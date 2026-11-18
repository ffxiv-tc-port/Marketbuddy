using System;
using System.Collections.Generic;
using System.Numerics;
using Marketbuddy;

// 情境測試：側邊面板的基準點、縮放、偏移、主視窗位置與同一欄疊放。
// 負對照是改版前的寫法（偏移一律從右上角量、面板左上角對齊）：放在左側時面板一變寬就蓋到原生視窗。
// 執行：dotnet run -c Release --project tests/PanelPlacementTests/PanelPlacementTests.csproj
// 回傳碼：0＝全部通過；1＝有情境失敗。

internal static class Program
{
    private static int failures;
    private static int passes;

    private static readonly Vector2 NativeTopLeft = new(800, 300);
    private static readonly Vector2 NativeRaw = new(600, 500);

    private static int Main()
    {
        DefaultMatchesLegacy();
        MissingKeyIsLegacy();
        ScaleIsApplied();
        ViewportOriginIsAdded();
        CornersAndPivots();
        LeftSideHugsNativeAtAnyWidth();
        ScreenIgnoresNative();
        UnknownAnchorFallsBack();
        StackingBothDirections();
        BottomColumnEndToEnd();
        ListHeightCap();
        SwitchingAnchorResetsOffset();
        TopColumnHeightCap();
        LeftWidthNeverShrinks();
        ButtonSettleGate();

        Console.WriteLine($"PANELPLACEMENT_DONE pass={passes} fail={failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>改版前 LiveSellList／重掛面板的位置公式（右上角＋偏移，樞紐左上）。</summary>
    private static Vector2 Legacy(Vector2 topLeft, Vector2 size, Vector2 offset)
        => new(topLeft.X + size.X + offset.X, topLeft.Y + offset.Y);

    /// <summary>ImGui 套樞紐之後視窗左上角的位置。</summary>
    private static Vector2 WindowTopLeft(PanelPlacement p, Vector2 windowSize) => p.Position - p.Pivot * windowSize;

    private static Vector2 Size(float scale) => NativeRaw * PanelPlacementMath.SafeScale(scale);

    private static void DefaultMatchesLegacy()
    {
        foreach (var offset in new[] { new Vector2(4, 0), new Vector2(-1161, 320), new Vector2(0, -50) })
        {
            var p = PanelPlacementMath.Compute(PanelAnchor.OutsideTopRight, NativeTopLeft, Size(1f), offset, Vector2.Zero);
            Check($"右上＝舊公式 offset={offset}", p.Position == Legacy(NativeTopLeft, Size(1f), offset) && p.Pivot == Vector2.Zero,
                $"pos={p.Position} pivot={p.Pivot}");
        }
    }

    private static void MissingKeyIsLegacy()
    {
        Check("default(PanelAnchor)＝右上外側", default(PanelAnchor) == PanelAnchor.OutsideTopRight, "");
        Check("右上不往上長", !PanelPlacementMath.GrowsUp(default), "");
    }

    private static void ScaleIsApplied()
    {
        var p = PanelPlacementMath.Compute(PanelAnchor.OutsideTopRight, NativeTopLeft, Size(1.5f), Vector2.Zero, Vector2.Zero);
        Check("縮放 1.5：右緣＝X＋寬×1.5", p.Position.X == 800 + 900, $"x={p.Position.X}");
        var b = PanelPlacementMath.Compute(PanelAnchor.OutsideBottomRight, NativeTopLeft, Size(1.5f), Vector2.Zero, Vector2.Zero);
        Check("縮放 1.5：下緣＝Y＋高×1.5", b.Position.Y == 300 + 750, $"y={b.Position.Y}");
        foreach (var bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check($"縮放 {bad} 當 1", PanelPlacementMath.SafeScale(bad) == 1f, "");
        Check("縮放 0.8 照用", PanelPlacementMath.SafeScale(0.8f) == 0.8f, "");
    }

    private static void ViewportOriginIsAdded()
    {
        var origin = new Vector2(-1920, 40);
        foreach (PanelAnchor a in Enum.GetValues(typeof(PanelAnchor)))
        {
            var at0 = PanelPlacementMath.Compute(a, NativeTopLeft, Size(1f), new Vector2(7, 9), Vector2.Zero);
            var atO = PanelPlacementMath.Compute(a, NativeTopLeft, Size(1f), new Vector2(7, 9), origin);
            Check($"{a}：主視窗位置整個平移", atO.Position - at0.Position == origin && atO.Pivot == at0.Pivot, "");
        }
    }

    private static void CornersAndPivots()
    {
        var s = Size(1f);
        Expect(PanelAnchor.OutsideTopRight, new(1400, 300), new(0, 0));
        Expect(PanelAnchor.OutsideBottomRight, new(1400, 800), new(0, 1));
        Expect(PanelAnchor.OutsideTopLeft, new(800, 300), new(1, 0));
        Expect(PanelAnchor.OutsideBottomLeft, new(800, 800), new(1, 1));
        foreach (PanelAnchor a in Enum.GetValues(typeof(PanelAnchor)))
        {
            var p = PanelPlacementMath.Compute(a, NativeTopLeft, s, Vector2.Zero, Vector2.Zero);
            Check($"{a}：GrowsUp 兩處一致", p.GrowsUp == PanelPlacementMath.GrowsUp(a), "");
        }

        void Expect(PanelAnchor a, Vector2 pos, Vector2 pivot)
        {
            var p = PanelPlacementMath.Compute(a, NativeTopLeft, s, Vector2.Zero, Vector2.Zero);
            Check($"{a} 位置與樞紐", p.Position == pos && p.Pivot == pivot, $"pos={p.Position} pivot={p.Pivot}");
        }
    }

    // 使用者實際的用法：把整欄搬到原生視窗左邊。舊寫法只能用負偏移從右上角量過去，面板寬度一變就偏。
    private static void LeftSideHugsNativeAtAnyWidth()
    {
        var s = Size(1f);
        var offset = PanelPlacementMath.DefaultOffset(PanelAnchor.OutsideTopLeft);
        var legacyOffset = new Vector2(-(s.X + 300 + 4), 0); // 照寬 300 的面板調好的舊偏移
        foreach (var width in new[] { 300f, 520f, 900f })
        {
            var p = PanelPlacementMath.Compute(PanelAnchor.OutsideTopLeft, NativeTopLeft, s, offset, Vector2.Zero);
            var right = WindowTopLeft(p, new Vector2(width, 200)).X + width;
            Check($"左側：寬 {width} 右緣仍貼原生左緣", right == NativeTopLeft.X - 4, $"right={right}");

            var legacyRight = Legacy(NativeTopLeft, s, legacyOffset).X + width;
            var legacyHugs = legacyRight == NativeTopLeft.X - 4;
            Check($"左側舊寫法（負對照）：寬 {width} {(width == 300 ? "剛好貼齊" : "偏掉")}",
                legacyHugs == (width == 300), $"legacyRight={legacyRight}");
        }

        var big = Size(1.4f);
        var pb = PanelPlacementMath.Compute(PanelAnchor.OutsideBottomLeft, NativeTopLeft, big, offset, Vector2.Zero);
        var tl = WindowTopLeft(pb, new Vector2(450, 260));
        Check("左下：縮放 1.4 時右緣貼左緣、下緣貼下緣",
            tl.X + 450 == NativeTopLeft.X - 4 && tl.Y + 260 == NativeTopLeft.Y + big.Y, $"tl={tl}");
    }

    private static void ScreenIgnoresNative()
    {
        var a = PanelPlacementMath.Compute(PanelAnchor.Screen, NativeTopLeft, Size(1f), new Vector2(50, 60), new Vector2(10, 0));
        var b = PanelPlacementMath.Compute(PanelAnchor.Screen, new Vector2(5, 5), Size(2f), new Vector2(50, 60), new Vector2(10, 0));
        Check("固定位置：與原生視窗無關", a == b && a.Position == new Vector2(60, 60) && a.Pivot == Vector2.Zero, $"a={a}");
    }

    private static void UnknownAnchorFallsBack()
    {
        foreach (var raw in new[] { -1, 5, 99 })
        {
            var a = (PanelAnchor)raw;
            var p = PanelPlacementMath.Compute(a, NativeTopLeft, Size(1f), new Vector2(4, 0), Vector2.Zero);
            Check($"不認得的值 {raw} 退回右上",
                PanelPlacementMath.Normalize(a) == PanelAnchor.OutsideTopRight &&
                p.Position == Legacy(NativeTopLeft, Size(1f), new Vector2(4, 0)), $"pos={p.Position}");
        }
    }

    private static void StackingBothDirections()
    {
        var down = PanelPlacementMath.Compute(PanelAnchor.OutsideTopRight, NativeTopLeft, Size(1f), Vector2.Zero, Vector2.Zero);
        Check("往下長：沒有鄰居就貼基準點", PanelPlacementMath.Stack(down, null) == down, "");
        Check("往下長：接在下緣＋4（＝舊行為）", PanelPlacementMath.Stack(down, 420f).Position == new Vector2(1400, 424), "");
        var up = PanelPlacementMath.Compute(PanelAnchor.OutsideBottomLeft, NativeTopLeft, Size(1f), Vector2.Zero, Vector2.Zero);
        var su = PanelPlacementMath.Stack(up, 600f);
        Check("往上長：接在上緣－4、樞紐不變", su.Position == new Vector2(800, 596) && su.Pivot == up.Pivot, $"{su}");
        Check("外緣：往下長取下緣", PanelPlacementMath.OuterEdgeY(down, new(0, 300), new(10, 120)) == 420f, "");
        Check("外緣：往上長取上緣", PanelPlacementMath.OuterEdgeY(up, new(0, 680), new(10, 120)) == 680f, "");
    }

    // 下緣基準點：重掛面板先畫（貼下緣），即時掛單接在它上面；兩塊不重疊、間隙 4，整欄總高不超過原生視窗高。
    private static void BottomColumnEndToEnd()
    {
        var column = PanelPlacementMath.Compute(PanelAnchor.OutsideBottomRight, NativeTopLeft, Size(1f),
            PanelPlacementMath.DefaultOffset(PanelAnchor.OutsideBottomRight), Vector2.Zero);
        var repriceSize = new Vector2(250, 150);
        var repriceTl = WindowTopLeft(PanelPlacementMath.Stack(column, null), repriceSize);
        var edge = PanelPlacementMath.OuterEdgeY(column, repriceTl, repriceSize);
        var listPlacement = PanelPlacementMath.Stack(column, edge);
        var cap = PanelPlacementMath.ListMaxHeight(Size(1f).Y, column, listPlacement, 200f);
        var listSize = new Vector2(400, Math.Min(900f, cap));
        var listTl = WindowTopLeft(listPlacement, listSize);
        Check("下緣：重掛面板下緣貼原生下緣", repriceTl.Y + repriceSize.Y == 800, $"repriceTl={repriceTl}");
        Check("下緣：即時掛單在重掛面板上方、間隙 4", listTl.Y + listSize.Y == repriceTl.Y - 4, $"listTl={listTl}");
        Check("下緣：兩塊左緣都在原生右緣＋4", listTl.X == 1404 && repriceTl.X == 1404, "");
        var total = repriceTl.Y + repriceSize.Y - listTl.Y;
        Check("下緣：整欄總高 ≤ 原生視窗高（很長的清單）", total <= Size(1f).Y, $"total={total}");

        // 負對照：改版前即時掛單先畫、貼基準點，高度上限沒扣重掛面板 ⇒ 整欄超出原生視窗上緣。
        var oldListCap = PanelPlacementMath.ListMaxHeight(Size(1f).Y, column, PanelPlacementMath.Stack(column, null), 200f);
        var oldTotal = Math.Min(900f, oldListCap) + PanelPlacementMath.Gap + repriceSize.Y;
        Check("負對照：舊順序整欄超高", oldTotal > Size(1f).Y, $"oldTotal={oldTotal}");
    }

    // 上緣基準點的對稱情形：整欄總高同樣 ≤ 原生視窗高。
    private static void TopColumnHeightCap()
    {
        var column = PanelPlacementMath.Compute(PanelAnchor.OutsideTopRight, NativeTopLeft, Size(1f), new Vector2(4, 0), Vector2.Zero);
        var repriceSize = new Vector2(250, 150);
        var repriceTl = WindowTopLeft(PanelPlacementMath.Stack(column, null), repriceSize);
        var listPlacement = PanelPlacementMath.Stack(column, PanelPlacementMath.OuterEdgeY(column, repriceTl, repriceSize));
        var cap = PanelPlacementMath.ListMaxHeight(Size(1f).Y, column, listPlacement, 200f);
        var listTl = WindowTopLeft(listPlacement, new Vector2(400, cap));
        Check("上緣：整欄總高 ≤ 原生視窗高", listTl.Y + cap - repriceTl.Y <= Size(1f).Y, $"cap={cap}");
    }

    // 左側基準點寬度只增不減；右側不設下限。
    private static void LeftWidthNeverShrinks()
    {
        var left = PanelPlacementMath.Compute(PanelAnchor.OutsideBottomLeft, NativeTopLeft, Size(1f), new Vector2(-4, 0), Vector2.Zero);
        var right = PanelPlacementMath.Compute(PanelAnchor.OutsideTopRight, NativeTopLeft, Size(1f), new Vector2(4, 0), Vector2.Zero);
        Check("左側：下限＝最寬", PanelPlacementMath.MinWidth(left, 320f) == 320f, "");
        Check("左側：還沒量過＝0", PanelPlacementMath.MinWidth(left, 0f) == 0f, "");
        Check("左側：NaN＝0", PanelPlacementMath.MinWidth(left, float.NaN) == 0f, "");
        Check("右側：不設下限", PanelPlacementMath.MinWidth(right, 320f) == 0f, "");
        // 量到的內容寬依序 300→320→280：右緣貼著時左緣停在 320 寬的位置，不回縮。
        var widest = 0f;
        var lefts = new List<float>();
        foreach (var measured in new[] { 300f, 320f, 280f })
        {
            var drawn = Math.Max(measured, PanelPlacementMath.MinWidth(left, widest));
            lefts.Add(WindowTopLeft(left, new Vector2(drawn, 100)).X);
            widest = Math.Max(widest, drawn);
        }

        Check("左側：內容變窄時左緣不回縮", lefts[2] == lefts[1] && lefts[1] < lefts[0], string.Join(",", lefts));
    }

    // 市場動作按鈕：剛出現或剛移位 300 ms 內不接受點擊。
    private static void ButtonSettleGate()
    {
        var settle = new ButtonSettle();
        var t0 = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var p = new Vector2(100, 200);
        Check("按鈕：剛出現不接受", !settle.Observe("a", p, t0), "");
        Check("按鈕：299 ms 仍不接受", !settle.Observe("a", p, t0.AddMilliseconds(299)), "");
        Check("按鈕：300 ms 接受", settle.Observe("a", p, t0.AddMilliseconds(300)), "");
        Check("按鈕：次像素抖動不算移位", settle.Observe("a", p + new Vector2(0.3f, -0.4f), t0.AddMilliseconds(316)), "");
        Check("按鈕：移位後不接受", !settle.Observe("a", p + new Vector2(0, 20), t0.AddMilliseconds(333)), "");
        Check("按鈕：移位後 300 ms 接受", settle.Observe("a", p + new Vector2(0, 20), t0.AddMilliseconds(633)), "");
        Check("按鈕：隔太久沒畫再出現＝剛出現", !settle.Observe("a", p + new Vector2(0, 20), t0.AddMilliseconds(1000)), "");
        Check("按鈕：各自的鍵互不影響", !settle.Observe("b", p, t0.AddMilliseconds(1000)), "");
    }

    private static void ListHeightCap()
    {
        var column = new PanelPlacement(new Vector2(0, 300), Vector2.Zero);
        Check("高度上限：沒有疊放＝原生高", PanelPlacementMath.ListMaxHeight(500, column, column, 200) == 500, "");
        var stacked = PanelPlacementMath.Stack(column, 450);
        Check("高度上限：扣掉上面那塊（＝舊公式）",
            PanelPlacementMath.ListMaxHeight(500, column, stacked, 200) == 500 - (454 - 300), "");
        Check("高度上限：地板 200", PanelPlacementMath.ListMaxHeight(500, column, PanelPlacementMath.Stack(column, 900), 200) == 200, "");
        Check("高度上限：NaN 走地板", PanelPlacementMath.ListMaxHeight(float.NaN, column, column, 200) == 200, "");
    }

    private static void SwitchingAnchorResetsOffset()
    {
        var s = Size(1f);
        var user = new Vector2(-1161, 320);
        Check("換到左上：偏移重設成往左 4",
            PanelPlacementMath.OffsetAfterSwitch(PanelAnchor.OutsideTopRight, PanelAnchor.OutsideTopLeft, user, true, NativeTopLeft, s)
            == new Vector2(-4, 0), "");
        Check("換到右下：偏移重設成往右 4",
            PanelPlacementMath.OffsetAfterSwitch(PanelAnchor.OutsideTopRight, PanelAnchor.OutsideBottomRight, user, true, NativeTopLeft, s)
            == new Vector2(4, 0), "");
        var keep = PanelPlacementMath.OffsetAfterSwitch(PanelAnchor.OutsideTopRight, PanelAnchor.Screen, user, true, NativeTopLeft, s);
        Check("換到固定位置（視窗開著）：從原本那一點開始",
            keep == Legacy(NativeTopLeft, s, user), $"keep={keep}");
        Check("換到固定位置（視窗沒開）：預設 100,100",
            PanelPlacementMath.OffsetAfterSwitch(PanelAnchor.OutsideTopRight, PanelAnchor.Screen, user, false, NativeTopLeft, s)
            == new Vector2(100, 100), "");
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            passes++;
            return;
        }

        failures++;
        Console.WriteLine($"FAIL {name} {detail}");
    }
}
