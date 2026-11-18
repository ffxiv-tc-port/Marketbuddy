using System.Numerics;

namespace Marketbuddy
{
    /// <summary>
    /// 側邊面板貼在原生視窗的哪一個基準點。數值會寫進設定檔：<b>不可改號、不可重排</b>。
    /// 0＝右側外緣上緣對齊＝改版前唯一的行為，所以缺鍵與 <c>default</c> 都落在舊位置。
    /// </summary>
    public enum PanelAnchor
    {
        OutsideTopRight = 0,
        OutsideBottomRight = 1,
        OutsideTopLeft = 2,
        OutsideBottomLeft = 3,
        Screen = 4,
    }

    /// <summary>ImGui 視窗這一幀的位置與樞紐（<c>SetNextWindowPos</c> 的 pos／pivot）。</summary>
    internal readonly record struct PanelPlacement(Vector2 Position, Vector2 Pivot)
    {
        /// <summary>樞紐在下緣＝這一欄往上長，疊在它上面的面板要接在它的上緣。</summary>
        public bool GrowsUp => Pivot.Y >= 0.5f;
    }

    /// <summary>
    /// 側邊面板位置的純計算，不碰 ImGui 與遊戲記憶體（離線測試直接編這個檔）。
    /// 輸入一律是「這一幀」量到的值；呼叫端每幀重算，不保存任何原生指標。
    /// 座標系：原生視窗的位置是遊戲畫面座標，ImGui 是桌面座標，兩者差主視窗位置。
    /// </summary>
    internal static class PanelPlacementMath
    {
        /// <summary>面板與原生視窗之間、以及同一欄兩塊面板之間的間隙。</summary>
        public const float Gap = 4f;

        /// <summary>設定檔裡出現不認得的數值（手改或更新版寫入）時退回舊行為。</summary>
        public static PanelAnchor Normalize(PanelAnchor anchor)
            => anchor is >= PanelAnchor.OutsideTopRight and <= PanelAnchor.Screen
                ? anchor
                : PanelAnchor.OutsideTopRight;

        /// <summary>基準點在原生視窗下緣＝這一欄往上長（與 <see cref="PanelPlacement.GrowsUp"/> 一致）。</summary>
        public static bool GrowsUp(PanelAnchor anchor)
            => Normalize(anchor) is PanelAnchor.OutsideBottomRight or PanelAnchor.OutsideBottomLeft;

        /// <summary>原生視窗的縮放；0、負數、NaN、無限大一律當 1。</summary>
        public static float SafeScale(float scale)
            => scale > 0f && float.IsFinite(scale) ? scale : 1f;

        /// <param name="nativeTopLeft">原生視窗左上角（遊戲畫面座標）。</param>
        /// <param name="nativeSize">原生視窗已乘上縮放的尺寸。</param>
        /// <param name="offset">使用者的偏移；<see cref="PanelAnchor.Screen"/> 時就是畫面座標。</param>
        /// <param name="viewportOrigin">ImGui 主視窗在桌面上的位置。</param>
        public static PanelPlacement Compute(PanelAnchor anchor, Vector2 nativeTopLeft, Vector2 nativeSize,
            Vector2 offset, Vector2 viewportOrigin)
        {
            var left = nativeTopLeft.X;
            var right = nativeTopLeft.X + nativeSize.X;
            var top = nativeTopLeft.Y;
            var bottom = nativeTopLeft.Y + nativeSize.Y;

            // 左側的樞紐 X＝1：面板右緣貼著原生視窗左緣，面板變寬只會往左長、不會蓋到原生視窗。
            var (point, pivot) = Normalize(anchor) switch
            {
                PanelAnchor.OutsideBottomRight => (new Vector2(right, bottom), new Vector2(0f, 1f)),
                PanelAnchor.OutsideTopLeft => (new Vector2(left, top), new Vector2(1f, 0f)),
                PanelAnchor.OutsideBottomLeft => (new Vector2(left, bottom), new Vector2(1f, 1f)),
                PanelAnchor.Screen => (Vector2.Zero, Vector2.Zero),
                _ => (new Vector2(right, top), Vector2.Zero),
            };

            return new PanelPlacement(viewportOrigin + point + offset, pivot);
        }

        /// <summary>
        /// 同一欄的第二塊面板：接在前一塊這一幀量到的外緣（往下長＝下緣、往上長＝上緣）。
        /// <paramref name="neighbourEdgeY"/> 為 null＝前一塊這一幀沒畫，直接用欄的基準點。
        /// </summary>
        public static PanelPlacement Stack(PanelPlacement column, float? neighbourEdgeY)
        {
            if (neighbourEdgeY is not { } edge)
                return column;
            var y = column.GrowsUp ? edge - Gap : edge + Gap;
            return column with { Position = new Vector2(column.Position.X, y) };
        }

        /// <summary>這一塊畫完之後，下一塊要接的外緣 Y。</summary>
        public static float OuterEdgeY(PanelPlacement column, Vector2 windowPos, Vector2 windowSize)
            => column.GrowsUp ? windowPos.Y : windowPos.Y + windowSize.Y;

        /// <summary>
        /// 掛單表的高度上限：原生視窗高度扣掉同一欄裡被別的面板吃掉的垂直空間，地板 <paramref name="floor"/>。
        /// </summary>
        public static float ListMaxHeight(float nativeHeight, PanelPlacement column, PanelPlacement list, float floor)
        {
            var used = System.Math.Abs(list.Position.Y - column.Position.Y);
            var available = nativeHeight - used;
            return float.IsFinite(available) && available > floor ? available : floor;
        }

        /// <summary>
        /// 使用者換基準點之後的偏移：舊偏移是對舊基準點量的，沿用會把面板丟到沒人指定過的位置，所以換成預設間隙；
        /// 換成固定位置且原生視窗開著時，從舊基準點這一幀算出的那一點開始（樞紐不在左上時是近似）。
        /// </summary>
        public static Vector2 OffsetAfterSwitch(PanelAnchor from, PanelAnchor to, Vector2 offset, bool nativeOpen,
            Vector2 nativeTopLeft, Vector2 nativeSize)
        {
            if (Normalize(to) != PanelAnchor.Screen || !nativeOpen)
                return DefaultOffset(to);
            return Compute(from, nativeTopLeft, nativeSize, offset, Vector2.Zero).Position;
        }

        /// <summary>
        /// 左側基準點（樞紐 X＝1）時面板的寬度下限：只增不減，面板變窄時右緣貼著、左緣不往回縮，裡面的按鈕不跟著橫移。
        /// 其餘基準點回 0（不設下限）。<paramref name="widest"/> 是這塊面板這次開著以來量到的最寬值。
        /// </summary>
        public static float MinWidth(PanelPlacement placement, float widest)
            => placement.Pivot.X >= 0.5f && float.IsFinite(widest) && widest > 0f ? widest : 0f;

        /// <summary>換基準點時改用的偏移：貼原生視窗的留一個間隙，左側往左。</summary>
        public static Vector2 DefaultOffset(PanelAnchor anchor)
            => Normalize(anchor) switch
            {
                PanelAnchor.OutsideTopLeft or PanelAnchor.OutsideBottomLeft => new Vector2(-Gap, 0f),
                PanelAnchor.Screen => new Vector2(100f, 100f),
                _ => new Vector2(Gap, 0f),
            };
    }

    /// <summary>
    /// 市場動作按鈕的防呆：同一顆按鈕的畫面位置最近 <see cref="SettleMs"/> 內變過（或剛出現）就不接受點擊，
    /// 免得面板移位讓別的按鈕剛好滑到游標底下。純防呆：只會讓點擊變少，不產生任何動作。只在 UI 執行緒用。
    /// </summary>
    internal sealed class ButtonSettle
    {
        public const double SettleMs = 300;

        /// <summary>位置差在這以內當成沒動（次像素的浮點抖動）。</summary>
        private const float Tolerance = 0.5f;

        private readonly System.Collections.Generic.Dictionary<string, (Vector2 Pos, System.DateTime MovedAt, System.DateTime SeenAt)> entries = new();

        /// <summary>記下這一幀的位置；回 true＝已經靜止夠久，可以接受點擊。</summary>
        public bool Observe(string key, Vector2 pos, System.DateTime now)
        {
            var movedAt = now;
            if (entries.TryGetValue(key, out var e) && (now - e.SeenAt).TotalMilliseconds <= SettleMs &&
                System.Math.Abs(e.Pos.X - pos.X) <= Tolerance && System.Math.Abs(e.Pos.Y - pos.Y) <= Tolerance)
                movedAt = e.MovedAt;
            entries[key] = (pos, movedAt, now);
            return (now - movedAt).TotalMilliseconds >= SettleMs;
        }
    }
}
