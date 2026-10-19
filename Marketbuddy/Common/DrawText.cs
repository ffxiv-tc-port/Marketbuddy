using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Marketbuddy.Common
{
    /// <summary>
    /// 畫面文字的快取：輸入相同就回先前組好的字串，不每幀重新格式化。
    /// 契約：只在繪製執行緒上呼叫（裡面是裸 Dictionary）；每張表滿 <see cref="Cap"/> 筆就整張清掉重來；
    /// 目前的 CultureInfo 換了就全部作廢（數字與日期照它格式化）。
    /// 每種用途有自己的鍵型別（＝自己的一張表），不同用途不會互撞。
    /// </summary>
    internal static class DrawText
    {
        /// <summary>要大於「同一幀裡會出現的不同鍵數」，否則每幀清掉重填（比價表一幀約一萬格價格）。</summary>
        private const int Cap = 32768;

        private static CultureInfo? culture;
        private static int generation;

        private static class Table<TKey>
            where TKey : notnull
        {
            internal static readonly Dictionary<TKey, string> Map = new();
            internal static int Generation;
        }

        private readonly record struct LocKey<TArgs>(string Template, TArgs Args);

        private readonly record struct NumberKey(string? Prefix, long Value);

        private readonly record struct IntKey(string? Prefix, int Value);

        private readonly record struct IntSuffixKey(int Value, string Suffix);

        private readonly record struct ConcatKey(string A, string B);

        private readonly record struct MinuteKey(string Format, long Minute);

        private static Dictionary<TKey, string> MapOf<TKey>()
            where TKey : notnull
        {
            var current = CultureInfo.CurrentCulture;
            if (!ReferenceEquals(current, culture))
            {
                culture = current;
                generation++;
            }

            var map = Table<TKey>.Map;
            if (Table<TKey>.Generation != generation || map.Count >= Cap)
            {
                map.Clear();
                Table<TKey>.Generation = generation;
            }

            return map;
        }

        private static string Memo<TKey>(TKey key, Func<TKey, string> make)
            where TKey : notnull
        {
            var map = MapOf<TKey>();
            if (!map.TryGetValue(key, out var text))
                map[key] = text = make(key);
            return text;
        }

        /// <summary>
        /// 帶參數的句子：樣板與參數相同就回同一個字串。
        /// 🔴 <paramref name="make"/> 傳 <c>static</c> lambda（捕捉變數的每次呼叫都配置），文字只能由樣板與參數決定；
        /// 同一個樣板配同一種參數型別時，所有呼叫點要用同一個 <paramref name="make"/>。
        /// </summary>
        internal static string Loc<TArgs>(string template, TArgs args, Func<string, TArgs, string> make)
        {
            var map = MapOf<LocKey<TArgs>>();
            var key = new LocKey<TArgs>(template, args);
            if (!map.TryGetValue(key, out var text))
                map[key] = text = make(template, args);
            return text;
        }

        /// <summary>等同 <c>template.Loc(value)</c>。</summary>
        internal static string Loc(string template, int value)
            => Loc(template, value, static (t, v) => t.Loc(v));

        /// <summary>等同 <c>template.Loc(value)</c>。</summary>
        internal static string Loc(string template, string value)
            => Loc(template, value, static (t, v) => t.Loc(v));

        /// <summary>等同 <c>template.Loc(a, b)</c>。</summary>
        internal static string Loc(string template, string a, string b)
            => Loc(template, (a, b), static (t, v) => t.Loc(v.a, v.b));

        /// <summary>等同 <c>value.ToString("N0")</c>。</summary>
        internal static string N0(long value)
            => Memo(new NumberKey(null, value), static k => k.Value.ToString("N0"));

        /// <summary>等同 <c>prefix + value.ToString("N0")</c>。</summary>
        internal static string N0(string prefix, long value)
            => Memo(new NumberKey(prefix, value), static k => k.Prefix + k.Value.ToString("N0"));

        /// <summary>等同 <c>value.ToString()</c>。</summary>
        internal static string Int(int value)
            => Memo(new IntKey(null, value), static k => k.Value.ToString());

        /// <summary>等同 <c>a + b</c>。</summary>
        internal static string Concat(string a, string b)
            => Memo(new ConcatKey(a, b), static k => k.A + k.B);

        /// <summary>等同 <c>a + b</c>。</summary>
        internal static string Concat(string a, int b)
            => Memo(new IntKey(a, b), static k => k.Prefix + k.Value);

        /// <summary>等同 <c>a + b</c>。</summary>
        internal static string Concat(int a, string b)
            => Memo(new IntSuffixKey(a, b), static k => k.Value + k.Suffix);

        /// <summary>
        /// 等同 <c>utc.ToLocalTime().ToString(format)</c>。
        /// 🔴 <paramref name="format"/> 不可以含秒或更細的欄位：鍵只精確到當地時間的「分」。
        /// </summary>
        internal static string LocalMinute(DateTime utc, string format)
            => Memo(new MinuteKey(format, utc.ToLocalTime().Ticks / TimeSpan.TicksPerMinute),
                static k => new DateTime(k.Minute * TimeSpan.TicksPerMinute).ToString(k.Format));

        /// <summary>
        /// <paramref name="builder"/> 的內容與 <paramref name="last"/> 相同就回 <paramref name="last"/>，
        /// 不同才配置新字串。給「先在可重用的 StringBuilder 裡組、內容多半沒變」的文字用。
        /// </summary>
        internal static string Reuse(ref string? last, StringBuilder builder)
        {
            if (last == null || !builder.Equals(last.AsSpan()))
                last = builder.ToString();
            return last;
        }
    }

    /// <summary>
    /// 單一顯示點的文字：鍵沒變就回上一次的字串。給鍵會一直換新值（進度數字）或鍵是參考（整份換掉的清單）的地方用，
    /// 這種鍵放進 <see cref="DrawText"/> 的表只會一直累積。
    /// 契約：放在非 readonly 的欄位裡、只在繪製執行緒上用；<c>make</c> 傳 <c>static</c> lambda。
    /// </summary>
    internal struct TextSlot<TKey>
    {
        private TKey key;
        private string? text;

        internal string Get(TKey newKey, Func<TKey, string> make)
        {
            if (text == null || !EqualityComparer<TKey>.Default.Equals(key, newKey))
            {
                key = newKey;
                text = make(newKey);
            }

            return text;
        }
    }
}
