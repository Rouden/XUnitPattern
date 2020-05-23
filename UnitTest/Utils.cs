﻿using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;
using System.Threading;

namespace XUnitPattern
{
    internal static class Utils
    {
        static private string cacheRepositoryRoot = "";
        static private SemaphoreSlim cacheRepositoryRootSemaphore = new SemaphoreSlim(1, 1);


        // リポジトリのルートディレクトリを得る
        internal static async Task<string> GetRepositoryRoot()
        {
            await cacheRepositoryRootSemaphore.WaitAsync();
            try
            {
                if (cacheRepositoryRoot != "") return cacheRepositoryRoot;

                string? path;
                path = await GetGitRepositoryRoot();
                if (path != null)
                {
                    cacheRepositoryRoot = path;
                    return path;
                }
                path = await GetSvnRepositoryRoot();
                if (path != null)
                {
                    cacheRepositoryRoot = path;
                    return path;
                }
                throw new Exception("The root directory not found.");
            }
            finally
            {
                cacheRepositoryRootSemaphore.Release();
            }
        }

        private static async Task<string?> GetGitRepositoryRoot()
        {
            // git rev-parse --show-toplevel コマンドでルートディレクトリを取得する
            string workingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location)!;
            string? gitPath = GetGitPath();
            if (gitPath == null) return null;
            var psi = new ProcessStartInfo(gitPath, "rev-parse --show-toplevel");
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = workingDirectory;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            var p = Process.Start(psi);
            string output = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode == 0)
            {
                return output.Trim('\n');
            }
            else
            {
                return null;
            }
        }

        private static async Task<string?> GetSvnRepositoryRoot()
        {
            // svn info --show-item wc-root コマンドでルートディレクトリを取得する
            // svn は WorkingDirectory がバージョン管理外のときにパスを取得できないので、ディレクトリを順にさかのぼって取得できるまで試行する.
            string workingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location)!;
            while (true)
            {
                var psi = new ProcessStartInfo("svn", "info --show-item wc-root");
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.WorkingDirectory = workingDirectory;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                var p = Process.Start(psi);
                string output = await p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    return output.Replace("\r\n", "");
                }
                else
                {
                    // workingDirectory を1階層上にする. 
                    var parent = Directory.GetParent(workingDirectory);
                    if (parent == null) return null;
                    workingDirectory = parent.FullName;
                }

            }
        }

        private static string? GetGitPath()
        {
            var p = Process.Start("git", "--version");
            p.WaitForExit();
            if (p.ExitCode == 0)
            {
                return "git";
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var dir in Directory.GetDirectories($@"{localAppData}/GitHub", "PortableGit_*"))
                {
                    return $@"{dir}/cmd/git.exe";
                }
            }
            return null;
        }

        static private string[] cacheVersionnedFiles = { };
        static private SemaphoreSlim cacheVersionnedFilesSemaphore = new SemaphoreSlim(1, 1);

        // バージョン管理されたファイルの一覧をフルパスで返す
        // svn はディレクトリをバージョン管理できるが、ディレクトリのパスは戻り値に含めない.
        internal static async Task<string[]> GetVersionedFiles()
        {
            await cacheVersionnedFilesSemaphore.WaitAsync();
            try
            {
                if (cacheVersionnedFiles.Length != 0) return cacheVersionnedFiles;

                string[]? files;
                files = await GetGitVersionedFiles();
                if (files != null)
                {
                    cacheVersionnedFiles = files;
                    return files;
                }
                files = await GetSvnVersionedFiles();
                if (files != null)
                {
                    cacheVersionnedFiles = files;
                    return files;
                }

                throw new Exception("Failed to get versioned files.");
            }
            finally
            {
                cacheVersionnedFilesSemaphore.Release();
            }
        }

        private static async Task<string[]?> GetGitVersionedFiles()
        {
            // git ls-files コマンドでファイルの一覧を取得する
            var root = await GetRepositoryRoot();
            string? gitPath = GetGitPath();
            if (gitPath == null) return null;
            var psi = new ProcessStartInfo(gitPath, "ls-files -z");
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            var p = Process.Start(psi);
            string output = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;

            // 一覧をフルパスの配列にして返す
            var files = output.Trim('\0').Split('\0');
            return files.Select(v => $"{root}/{v}").ToArray();
        }

        private static async Task<string[]?> GetSvnVersionedFiles()
        {
            // svn list -R コマンドでファイルの一覧を取得する
            var root = await GetRepositoryRoot();
            var psi = new ProcessStartInfo("svn", "list -R");
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            var p = Process.Start(psi);
            string output = await p.StandardOutput.ReadToEndAsync();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;

            // 一覧をフルパスの配列にして返す
            var files = output.Split("\r\n");
            return files.Select(v => $"{root}/{v}").Where(v=>!v.EndsWith("/")).ToArray();
        }

        // バージョン管理されたファイルで、指定の拡張子のファイルの一覧をフルパスで返す
        internal static async Task<string[]> GetVersionedFiles(string[] exts)
        {
            var list = await GetVersionedFiles();
            return list.Where(v => exts.Contains(Path.GetExtension(v))).ToArray();
        }

        // 拡張子のまとめ
        internal static string[] cppExts = new string[] { ".h", ".hpp", ".c", ".cpp" };
        internal static string[] csExts = new string[] { ".cs" };
        internal static string[] jstsExts = new string[] { ".js", ".ts", ".jsx", ".tsx" };
        internal static string[] webExts = jstsExts.Union(new string[] { ".htm", ".html", ".css" }).ToArray();
        internal static string[] generalExts = new string[] {".md", ".txt", ".xml"};
        internal static string[] textExts = cppExts.Union(cppExts).Union(csExts).Union(webExts).Union(generalExts).ToArray();
    }
}
