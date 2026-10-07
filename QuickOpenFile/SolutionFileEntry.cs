using System.IO;

namespace QuickOpenFile
{
    /// <summary>솔루션에 포함된 파일 하나.</summary>
    public sealed class SolutionFileEntry
    {
        public SolutionFileEntry(string fullPath, string project, string rootDirectory)
        {
            FullPath = fullPath;
            Project = project ?? string.Empty;
            FileName = Path.GetFileName(fullPath);
            NameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            Extension = Path.GetExtension(fullPath);

            string dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            RelativePath = MakeRelative(fullPath, rootDirectory);
            Directory = MakeRelative(dir, rootDirectory);
        }

        public string FullPath { get; }

        public string FileName { get; }

        public string NameWithoutExtension { get; }

        public string Extension { get; }

        /// <summary>솔루션 폴더 기준 상대 경로 (솔루션 폴더 밖이면 전체 경로).</summary>
        public string RelativePath { get; }

        /// <summary>목록의 Path 컬럼에 표시할 폴더.</summary>
        public string Directory { get; }

        public string Project { get; }

        private static string MakeRelative(string path, string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                return path;
            }

            string normalizedRoot = root.EndsWith("\\") ? root : root + "\\";
            if (path.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(normalizedRoot.Length);
            }

            if (string.Equals(path, root.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return path;
        }
    }
}
