using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickOpenFile
{
    /// <summary>
    /// C/C++ 파일에 대응하는 헤더 ↔ 소스 파일을 찾는다.
    /// 검색 순서:
    /// 1. 같은 폴더
    /// 2. 솔루션에 포함된 파일 중 같은 이름 (현재 파일과 경로가 가장 비슷한 것 우선)
    /// 3. 흔한 폴더 구조 (include ↔ src 등) 의 형제 폴더
    /// </summary>
    public static class HeaderSourceResolver
    {
        private static readonly string[] HeaderExtensions = { ".h", ".hpp", ".hh", ".hxx", ".h++", ".inl", ".ipp", ".tpp" };
        private static readonly string[] SourceExtensions = { ".cpp", ".c", ".cc", ".cxx", ".c++", ".cp", ".m", ".mm", ".cu" };

        private static readonly string[] SiblingDirectoryNames = { "include", "inc", "src", "source", "sources", "public", "private", "Public", "Private" };

        public static bool IsHeader(string path) => HasExtension(path, HeaderExtensions);

        public static bool IsSource(string path) => HasExtension(path, SourceExtensions);

        public static bool IsCppFile(string path) => IsHeader(path) || IsSource(path);

        /// <summary>대응하는 파일 경로, 없으면 null.</summary>
        public static string FindCounterpart(string currentPath, IReadOnlyList<SolutionFileEntry> solutionFiles)
        {
            if (string.IsNullOrEmpty(currentPath))
            {
                return null;
            }

            string[] targetExtensions;
            if (IsHeader(currentPath))
            {
                targetExtensions = SourceExtensions;
            }
            else if (IsSource(currentPath))
            {
                targetExtensions = HeaderExtensions;
            }
            else
            {
                return null;
            }

            string directory = Path.GetDirectoryName(currentPath) ?? string.Empty;
            string baseName = Path.GetFileNameWithoutExtension(currentPath);

            // 1. 같은 폴더
            string found = FindInDirectory(directory, baseName, targetExtensions, currentPath);
            if (found != null)
            {
                return found;
            }

            // 2. 솔루션 파일 목록
            if (solutionFiles != null)
            {
                SolutionFileEntry best = solutionFiles
                    .Where(f => string.Equals(f.NameWithoutExtension, baseName, StringComparison.OrdinalIgnoreCase) &&
                                Array.IndexOf(targetExtensions, f.Extension.ToLowerInvariant()) >= 0 &&
                                !string.Equals(f.FullPath, currentPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => CommonPrefixLength(f.FullPath, currentPath))
                    .ThenBy(f => Array.IndexOf(targetExtensions, f.Extension.ToLowerInvariant()))
                    .FirstOrDefault();

                if (best != null)
                {
                    return best.FullPath;
                }
            }

            // 3. 상위 폴더들의 include/src 형제 폴더 (예: lib/include/foo.h ↔ lib/src/foo.cpp)
            string parent = Path.GetDirectoryName(directory);
            for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(parent); depth++)
            {
                foreach (string sibling in SiblingDirectoryNames)
                {
                    string candidateDir = Path.Combine(parent, sibling);
                    if (string.Equals(candidateDir, directory, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidateDir))
                    {
                        continue;
                    }

                    found = FindInDirectory(candidateDir, baseName, targetExtensions, currentPath);
                    if (found != null)
                    {
                        return found;
                    }
                }

                parent = Path.GetDirectoryName(parent);
            }

            return null;
        }

        private static string FindInDirectory(string directory, string baseName, string[] extensions, string exclude)
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(directory, baseName + ext);
                if (!string.Equals(candidate, exclude, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool HasExtension(string path, string[] extensions)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path);
            return extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int length = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
            {
                i++;
            }

            return i;
        }
    }
}
