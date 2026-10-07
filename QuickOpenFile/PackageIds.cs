using System;

namespace QuickOpenFile
{
    /// <summary>QuickOpenFilePackage.vsct 의 Symbols 와 반드시 일치해야 한다.</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "21205e0d-3e33-4051-8ba4-e6ce4d95654c";
        public const string CmdSetString = "a75a3bdf-60ce-4733-be77-2ade041287ae";

        public static readonly Guid CmdSet = new Guid(CmdSetString);
    }

    internal static class PackageIds
    {
        public const int OpenFileInSolution = 0x0100;
        public const int ToggleHeaderSource = 0x0101;
        public const int GotoDeclImpl = 0x0102;
    }
}
