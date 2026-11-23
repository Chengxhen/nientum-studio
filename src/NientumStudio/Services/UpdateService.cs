using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace NientumStudio.Services
{
    /// <summary>
    /// 制谱器的 GitHub 更新服务。仓库固定为 Chengxhen/nientum-studio。
    /// 检测走 api.github.com/repos/.../releases；下载优先直连，失败后依次尝试 ghfast.top / ghproxy.net 镜像。
    /// 替换由退出后运行的 PowerShell 脚本完成，保留用户设置、主题、日志与 CustomCharts，失败自动回滚。
    /// </summary>
    public static class UpdateService
    {
        public const string Owner = "Chengxhen";
        public const string Repo = "nientum-studio";
        public const string RepoUrl = "https://github.com/Chengxhen/nientum-studio";
        public const string ApiReleases = "https://api.github.com/repos/Chengxhen/nientum-studio/releases?per_page=8";

        private static readonly string[] DownloadMirrors =
        {
            "",                          // 原始 GitHub 地址
            "https://ghfast.top/",       // 镜像前缀 + 原始地址
            "https://ghproxy.net/"
        };

        public sealed class UpdateAsset
        {
            public string Name { get; set; }
            public long Size { get; set; }
            public string Url { get; set; }
        }

        public sealed class UpdateRelease
        {
            public string Tag { get; set; }
            public string Name { get; set; }
            public string Body { get; set; }
            public DateTime? PublishedAt { get; set; }
            public string PreviousTag { get; set; }
            public UpdateAsset Full { get; set; }
            public UpdateAsset Patch { get; set; }
            public UpdateAsset Sha { get; set; }
            public bool IsNewer { get; set; }
            public bool CanUsePatch { get; set; }
            public string PageUrl => $"{RepoUrl}/releases/tag/{Tag}";
        }

        public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

        /// <summary>安装根目录：发行包布局下是「编辑器」的上一级，开发目录下就是 exe 所在目录。</summary>
        public static string InstallRoot
        {
            get
            {
                var parent = Directory.GetParent(ExeDir);
                if (parent != null && string.Equals(Path.GetFileName(ExeDir), "编辑器", StringComparison.OrdinalIgnoreCase))
                    return parent.FullName;
                return ExeDir;
            }
        }

        /// <summary>exe 目录相对安装根目录的路径（正常发行包为「编辑器」）。</summary>
        public static string ExeRel => Path.GetRelativePath(InstallRoot, ExeDir).Replace('/', '\\');

        public static string CacheDir => Path.Combine(InstallRoot, "_update_cache");

        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var attr = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    string raw = attr?.InformationalVersion ?? typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                    int plus = raw.IndexOf('+');
                    if (plus >= 0) raw = raw.Substring(0, plus);
                    raw = raw.TrimStart('v', 'V');
                    if (Version.TryParse(raw, out var v)) return v;
                }
                catch { }
                return new Version(0, 0, 0, 0);
            }
        }

        public static string CurrentVersionText => $"Exp-v1 · {CurrentVersion}";

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NientumStudio-Updater", CurrentVersion.ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static async Task<UpdateRelease> CheckAsync(CancellationToken ct = default)
        {
            string json;
            using (var client = CreateClient(TimeSpan.FromSeconds(25)))
            {
                using var resp = await client.GetAsync(ApiReleases, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub 接口返回 {(int)resp.StatusCode} {resp.ReasonPhrase}");
                json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }

            var arr = JArray.Parse(json);
            JObject latest = null, previous = null;
            foreach (var token in arr.OfType<JObject>())
            {
                if (token.Value<bool?>("draft") == true || token.Value<bool?>("prerelease") == true) continue;
                if (latest == null) { latest = token; continue; }
                previous = token;
                break;
            }
            if (latest == null) throw new InvalidOperationException("仓库里没有正式 Release。");

            var release = new UpdateRelease
            {
                Tag = latest.Value<string>("tag_name") ?? "",
                Name = latest.Value<string>("name") ?? "",
                Body = latest.Value<string>("body") ?? "",
                PublishedAt = latest.Value<DateTime?>("published_at"),
                PreviousTag = previous?.Value<string>("tag_name") ?? ""
            };

            var assets = (latest["assets"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
            foreach (var a in assets)
            {
                string name = a.Value<string>("name") ?? "";
                string state = a.Value<string>("state") ?? "";
                if (!state.Equals("uploaded", StringComparison.OrdinalIgnoreCase)) continue;
                var info = new UpdateAsset
                {
                    Name = name,
                    Size = a.Value<long?>("size") ?? 0,
                    Url = a.Value<string>("browser_download_url") ?? ""
                };
                if (name.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) release.Sha = info;
                else if (name.EndsWith("_patch.zip", StringComparison.OrdinalIgnoreCase)) release.Patch = info;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    bool preferred = name.IndexOf("NientumStudio", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool currentPreferred = release.Full != null &&
                                            release.Full.Name.IndexOf("NientumStudio", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (release.Full == null || (preferred && !currentPreferred) ||
                        (preferred == currentPreferred && info.Size > release.Full.Size))
                        release.Full = info;
                }
            }

            var cur = CurrentVersion;
            var latestVersion = ParseTag(release.Tag);
            release.IsNewer = latestVersion != null && latestVersion > cur;
            var prevVersion = ParseTag(release.PreviousTag);
            release.CanUsePatch = release.Patch != null && prevVersion != null && prevVersion == cur &&
                                  string.Equals(ExeRel, "编辑器", StringComparison.OrdinalIgnoreCase);
            return release;
        }

        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string raw = tag.Trim().TrimStart('v', 'V');
            int dash = raw.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) raw = raw.Substring(0, dash);
            return Version.TryParse(raw, out var v) ? v : null;
        }

        /// <summary>下载资产到缓存目录；返回本地路径。已存在且 SHA 校验通过时直接复用。</summary>
        public static async Task<string> DownloadAsync(UpdateRelease release, UpdateAsset asset, bool wantPatch,
                                                       IProgress<(long received, long total)> progress,
                                                       CancellationToken ct = default)
        {
            if (asset == null) throw new InvalidOperationException("此 Release 没有可用的下载附件。");
            Directory.CreateDirectory(CacheDir);
            string dest = Path.Combine(CacheDir, Path.GetFileName(asset.Name));
            string sha = await TryFetchExpectedShaAsync(release, asset.Name, ct).ConfigureAwait(false);

            if (File.Exists(dest) && asset.Size > 0 && new FileInfo(dest).Length == asset.Size)
            {
                if (string.IsNullOrEmpty(sha) || VerifySha256(dest, sha)) return dest;
                try { File.Delete(dest); } catch { }
            }

            Exception last = null;
            foreach (string prefix in DownloadMirrors)
            {
                ct.ThrowIfCancellationRequested();
                string url = prefix + asset.Url;
                try
                {
                    await DownloadOneAsync(url, dest, asset.Size, progress, ct).ConfigureAwait(false);
                    if (asset.Size > 0 && new FileInfo(dest).Length != asset.Size)
                        throw new IOException($"下载大小不符：{new FileInfo(dest).Length} / {asset.Size}");
                    if (!string.IsNullOrEmpty(sha) && !VerifySha256(dest, sha))
                        throw new InvalidDataException("SHA256 校验失败。");
                    return dest;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    last = ex;
                    AppLog.Warn($"[Update] 下载失败 {url}：{ex.Message}");
                }
            }
            throw new InvalidOperationException("所有下载地址都失败了。" + (last != null ? " " + last.Message : ""));
        }

        private static async Task DownloadOneAsync(string url, string dest, long expectedSize,
                                                   IProgress<(long, long)> progress, CancellationToken ct)
        {
            using var client = CreateClient(TimeSpan.FromMinutes(30));
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? expectedSize;
            string tmp = dest + ".part";
            try
            {
                using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        received += read;
                        progress?.Report((received, total));
                    }
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static async Task<string> TryFetchExpectedShaAsync(UpdateRelease release, string fileName, CancellationToken ct)
        {
            if (release.Sha == null || string.IsNullOrEmpty(release.Sha.Url)) return null;
            try
            {
                using var client = CreateClient(TimeSpan.FromSeconds(20));
                using var resp = await client.GetAsync(release.Sha.Url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string name = parts[^1].TrimStart('*');
                        if (string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase))
                            return parts[0].Trim().ToLowerInvariant();
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn($"[Update] 读取 SHA256SUMS 失败：{ex.Message}"); }
            return null;
        }

        private static bool VerifySha256(string file, string expected)
        {
            using var stream = File.OpenRead(file);
            using var sha = SHA256.Create();
            string actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>解压已下载的包并定位要覆盖的源目录：完整包返回安装根内容，增量包返回 payload。</summary>
        public static string PrepareSource(string archivePath, bool isPatch, IProgress<string> progress = null)
        {
            Directory.CreateDirectory(CacheDir);
            string extract = Path.Combine(CacheDir, isPatch ? "patch_extract" : "full_extract");
            if (Directory.Exists(extract))
            {
                try { Directory.Delete(extract, true); } catch { }
            }
            progress?.Report("正在解压更新包…");
            ZipFile.ExtractToDirectory(archivePath, extract, true);

            string source = isPatch ? FindPatchPayload(extract) : FindFullRoot(extract);
            if (source == null)
                throw new InvalidOperationException(isPatch ? "增量包结构不正确：找不到 payload。" : "完整包里找不到 编辑器\\NientumStudio.exe。");
            return source;
        }

        private static string FindFullRoot(string extract)
        {
            if (File.Exists(Path.Combine(extract, "编辑器", "NientumStudio.exe"))) return extract;
            foreach (string dir in Directory.EnumerateDirectories(extract, "*", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, "编辑器", "NientumStudio.exe"))) return dir;
            }
            return null;
        }

        private static string FindPatchPayload(string extract)
        {
            if (File.Exists(Path.Combine(extract, "payload", "编辑器", "NientumStudio.dll")))
                return Path.Combine(extract, "payload");
            foreach (string dir in Directory.EnumerateDirectories(extract, "payload", SearchOption.AllDirectories))
            {
                if (File.Exists(Path.Combine(dir, "编辑器", "NientumStudio.dll"))) return dir;
            }
            return null;
        }

        /// <summary>生成退出后执行的替换脚本并启动它；调用方随后应关闭编辑器。</summary>
        public static void BeginApply(string sourceDir, bool isPatch)
        {
            Directory.CreateDirectory(CacheDir);
            string script = Path.Combine(CacheDir, "apply_update.ps1");
            File.WriteAllText(script, BuildApplyScript(), new UTF8Encoding(true));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = CacheDir,
                Arguments = string.Join(" ", new[]
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                    "-File", Quote(script),
                    "-Root", Quote(InstallRoot),
                    "-Source", Quote(sourceDir),
                    "-Kind", Quote(isPatch ? "patch" : "full"),
                    "-ExeRel", Quote(ExeRel),
                    "-WaitPid", Process.GetCurrentProcess().Id.ToString()
                })
            };
            AppLog.Info($"[Update] 启动替换脚本，source={sourceDir}, kind={(isPatch ? "patch" : "full")}");
            Process.Start(psi);
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        /// <summary>清理上次更新留下的缓存，避免磁盘上堆积整包解压内容。</summary>
        public static void CleanupCache()
        {
            try
            {
                if (!Directory.Exists(CacheDir)) return;
                foreach (string dir in new[] { Path.Combine(CacheDir, "full_extract"), Path.Combine(CacheDir, "patch_extract") })
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                foreach (string file in Directory.EnumerateFiles(CacheDir, "*.zip"))
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
                }
            }
            catch (Exception ex) { AppLog.Warn($"[Update] 清理更新缓存失败：{ex.Message}"); }
        }

        private static string BuildApplyScript()
        {
            return @"param(
    [Parameter(Mandatory=$true)][string]$Root,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Kind,
    [Parameter(Mandatory=$true)][string]$ExeRel,
    [Parameter(Mandatory=$true)][int]$WaitPid
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $Root 'update_apply.log'
function Write-Log([string]$m) {
    try { Add-Content -LiteralPath $log -Value ('[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $m) -Encoding UTF8 } catch { }
}
Write-Log ('==== 开始更新 kind=' + $Kind + ', source=' + $Source + ', root=' + $Root)

# 1. 等待编辑器退出（最多 90 秒）
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Milliseconds 500

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backup = Join-Path $Root ('_update_backup_' + $stamp)
$preserveFiles = @('ui_settings.json','ui_theme.txt','studio_diag.log','studio_diag.log.old','portable.txt')
$preserveDirs = @('CustomCharts')

try {
    if (-not (Test-Path -LiteralPath $Source)) { throw ('更新源不存在: ' + $Source) }
    $files = Get-ChildItem -LiteralPath $Source -Recurse -File
    Write-Log ('待复制文件数: ' + $files.Count)

    foreach ($f in $files) {
        $rel = $f.FullName.Substring($Source.Length).TrimStart('\')
        $parts = $rel -split '\\'
        if ($preserveFiles -contains $parts[-1]) {
            Write-Log ('保留用户文件: ' + $rel)
            continue
        }
        if ($parts.Length -ge 2 -and $parts[0] -eq $ExeRel -and $preserveDirs -contains $parts[1]) {
            Write-Log ('保留用户目录: ' + $rel)
            continue
        }
        $dest = Join-Path $Root $rel
        if (Test-Path -LiteralPath $dest) {
            $bak = Join-Path $backup $rel
            $bakDir = Split-Path -Parent $bak
            if (-not (Test-Path -LiteralPath $bakDir)) { New-Item -ItemType Directory -Path $bakDir -Force | Out-Null }
            Copy-Item -LiteralPath $dest -Destination $bak -Force
        }
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
    }

    if ($Kind -eq 'patch') {
        $pdb = Join-Path $Root ($ExeRel + '\NientumStudio.pdb')
        if (Test-Path -LiteralPath $pdb) {
            Remove-Item -LiteralPath $pdb -Force -ErrorAction SilentlyContinue
            Write-Log '已删除 NientumStudio.pdb（与增量 DLL 不匹配）'
        }
    }
    Write-Log '更新成功'
}
catch {
    Write-Log ('更新失败: ' + $_.Exception.Message)
    if (Test-Path -LiteralPath $backup) {
        try {
            Get-ChildItem -LiteralPath $backup -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($backup.Length).TrimStart('\')
                $dest = Join-Path $Root $rel
                Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
            }
            Write-Log '已回滚到更新前文件'
        } catch { Write-Log ('回滚失败: ' + $_.Exception.Message) }
    }
}

# 2. 只保留最近两份备份
try {
    Get-ChildItem -LiteralPath $Root -Directory -Filter '_update_backup_*' |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 2 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} catch { }

# 3. 重新启动编辑器
$exe = Join-Path $Root ($ExeRel + '\NientumStudio.exe')
if (Test-Path -LiteralPath $exe) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
    Write-Log '已重新启动编辑器'
} else {
    Write-Log ('找不到编辑器 exe: ' + $exe)
}
";
        }
    }
}