using System;
using System.IO;

namespace Marketbuddy.Common
{
    // 只給 PriceSurveyLog／PriceSurveySnapshot／PriceSurveyWorldLog 編得過：Log 吞掉、設定目錄指到測試的暫存目錄。
    internal static class Dalamud
    {
        internal static readonly StubLog Log = new();
        internal static readonly StubPluginInterface PluginInterface = new();
    }

    internal sealed class StubLog
    {
        public void Information(string message)
        {
        }

        public void Information(Exception e, string message)
        {
        }
    }

    internal sealed class StubPluginInterface
    {
        public DirectoryInfo ConfigDirectory { get; set; } = new(Path.GetTempPath());
    }
}
