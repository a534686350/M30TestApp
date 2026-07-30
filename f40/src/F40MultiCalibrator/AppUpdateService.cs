using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace F40MultiCalibrator;

internal sealed record AppUpdateManifest(
	string Version,
	string PublishedAt,
	string Notes,
	string PackageName,
	string Sha256,
	string GiteeUrl,
	string GithubUrl);

internal sealed record AppUpdateInfo(AppUpdateManifest Manifest, string ManifestSource)
{
	public Version Version => System.Version.Parse(Manifest.Version);
}

internal static class AppUpdateService
{
	private const string GiteeManifestUrl = "https://gitee.com/hl515/m30-test-app/raw/main/f40/update.json";
	private const string GithubManifestUrl = "https://raw.githubusercontent.com/a534686350/M30TestApp/main/f40/update.json";
	private const string ExeName = "软件补偿与F40标定.exe";

	private static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(20)
	};

	static AppUpdateService()
	{
		Http.DefaultRequestHeaders.UserAgent.ParseAdd("F40MultiCalibrator-Updater/1.0");
	}

	public static Version CurrentVersion
	{
		get
		{
			Version? version = Assembly.GetEntryAssembly()?.GetName().Version;
			return version ?? new Version(1, 0, 0, 0);
		}
	}

	public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

	public static async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
	{
		List<string> errors = new List<string>();
		foreach ((string source, string url) in new[]
		{
			("Gitee", GiteeManifestUrl),
			("GitHub", GithubManifestUrl)
		})
		{
			try
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(source == "Gitee" ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(12));
				string json = await Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
				AppUpdateManifest? manifest = JsonSerializer.Deserialize<AppUpdateManifest>(json, new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				});
				ValidateManifest(manifest, source);
				return new AppUpdateInfo(manifest!, source);
			}
			catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
			{
				errors.Add(source + "：" + ex.Message);
			}
		}
		throw new InvalidOperationException("Gitee和GitHub更新源均不可用。" + Environment.NewLine + string.Join(Environment.NewLine, errors));
	}

	public static async Task<string> DownloadAsync(AppUpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
	{
		string workDir = Path.Combine(Path.GetTempPath(), "F40MultiCalibrator_update");
		Directory.CreateDirectory(workDir);
		string packageName = SanitizeFileName(info.Manifest.PackageName, "F40MultiCalibrator-update.zip");
		string packagePath = Path.Combine(workDir, packageName);
		List<string> errors = new List<string>();
		foreach ((string source, string url) in PackageUrls(info.Manifest))
		{
			try
			{
				if (File.Exists(packagePath))
				{
					File.Delete(packagePath);
				}
				await DownloadFileAsync(url, packagePath, progress, ct).ConfigureAwait(false);
				ValidatePackage(packagePath, info.Manifest.Sha256);
				return packagePath;
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				errors.Add(source + "：" + ex.Message);
			}
		}
		throw new InvalidOperationException("更新包下载失败。" + Environment.NewLine + string.Join(Environment.NewLine, errors));
	}

	public static void LaunchInstaller(string packagePath, string targetDir)
	{
		string workDir = Path.GetDirectoryName(packagePath) ?? Path.GetTempPath();
		string scriptPath = Path.Combine(workDir, "install-update.ps1");
		File.WriteAllText(scriptPath, InstallerScript, new UTF8Encoding(false));

		ProcessStartInfo start = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		start.ArgumentList.Add("-NoProfile");
		start.ArgumentList.Add("-ExecutionPolicy");
		start.ArgumentList.Add("Bypass");
		start.ArgumentList.Add("-File");
		start.ArgumentList.Add(scriptPath);
		start.ArgumentList.Add("-Zip");
		start.ArgumentList.Add(packagePath);
		start.ArgumentList.Add("-Target");
		start.ArgumentList.Add(targetDir);
		start.ArgumentList.Add("-ProcessId");
		start.ArgumentList.Add(Environment.ProcessId.ToString());
		start.ArgumentList.Add("-ExeName");
		start.ArgumentList.Add(ExeName);
		start.ArgumentList.Add("-WorkDir");
		start.ArgumentList.Add(workDir);
		if (Process.Start(start) == null)
		{
			throw new InvalidOperationException("无法启动更新安装程序。");
		}
	}

	private static IEnumerable<(string Source, string Url)> PackageUrls(AppUpdateManifest manifest)
	{
		if (!string.IsNullOrWhiteSpace(manifest.GiteeUrl))
		{
			yield return ("Gitee", manifest.GiteeUrl.Trim());
		}
		if (!string.IsNullOrWhiteSpace(manifest.GithubUrl))
		{
			yield return ("GitHub", manifest.GithubUrl.Trim());
		}
	}

	private static async Task DownloadFileAsync(string url, string destination, IProgress<int>? progress, CancellationToken ct)
	{
		using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		long total = response.Content.Headers.ContentLength ?? -1;
		await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		await using FileStream target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
		byte[] buffer = new byte[128 * 1024];
		long received = 0;
		int lastPercent = -1;
		int count;
		while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
		{
			await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
			received += count;
			if (total > 0 && progress != null)
			{
				int percent = (int)Math.Clamp(received * 100 / total, 0, 100);
				if (percent != lastPercent)
				{
					progress.Report(percent);
					lastPercent = percent;
				}
			}
		}
	}

	private static void ValidateManifest(AppUpdateManifest? manifest, string source)
	{
		if (manifest == null || !Version.TryParse(manifest.Version, out _))
		{
			throw new InvalidDataException(source + "更新清单版本号无效。");
		}
		if (string.IsNullOrWhiteSpace(manifest.PackageName) || string.IsNullOrWhiteSpace(manifest.Sha256))
		{
			throw new InvalidDataException(source + "更新清单缺少包名或SHA256。");
		}
		if (string.IsNullOrWhiteSpace(manifest.GiteeUrl) && string.IsNullOrWhiteSpace(manifest.GithubUrl))
		{
			throw new InvalidDataException(source + "更新清单没有下载地址。");
		}
	}

	private static void ValidatePackage(string path, string expectedSha256)
	{
		FileInfo info = new FileInfo(path);
		if (!info.Exists || info.Length == 0)
		{
			throw new InvalidDataException("下载的更新包为空。");
		}
		using (ZipArchive archive = ZipFile.OpenRead(path))
		{
			if (archive.Entries.Count == 0 || !archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), ExeName, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidDataException("更新包中没有主程序EXE。");
			}
		}
		using SHA256 sha = SHA256.Create();
		using FileStream stream = File.OpenRead(path);
		string actual = Convert.ToHexString(sha.ComputeHash(stream));
		string expected = RegexHex(expectedSha256);
		if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException($"更新包SHA256校验失败。期望={expected}，实际={actual}");
		}
	}

	private static string RegexHex(string value)
	{
		string result = new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
		if (result.Length != 64)
		{
			throw new InvalidDataException("更新清单SHA256格式无效。");
		}
		return result;
	}

	private static string SanitizeFileName(string name, string fallback)
	{
		string result = string.Join("_", (name ?? "").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
		return result.Length == 0 ? fallback : result;
	}

	private const string InstallerScript = """
param(
    [Parameter(Mandatory=$true)][string]$Zip,
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$ExeName,
    [Parameter(Mandatory=$true)][string]$WorkDir
)
$ErrorActionPreference = 'Stop'
$errorFile = Join-Path $WorkDir 'update-error.txt'
try {
    Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
    $stage = Join-Path $WorkDir 'stage'
    if (Test-Path -LiteralPath $stage) { [IO.Directory]::Delete($stage, $true) }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Expand-Archive -LiteralPath $Zip -DestinationPath $stage -Force
    $protected = @('setting', 'logs', '标定结果')
    foreach ($item in Get-ChildItem -LiteralPath $stage -Force) {
        if ($protected -contains $item.Name) { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $Target -Recurse -Force
    }
    if (Test-Path -LiteralPath $errorFile) { Remove-Item -LiteralPath $errorFile -Force }
    Start-Process -FilePath (Join-Path $Target $ExeName) -WorkingDirectory $Target
}
catch {
    Set-Content -LiteralPath $errorFile -Value ($_ | Out-String) -Encoding UTF8
    $existingExe = Join-Path $Target $ExeName
    if (Test-Path -LiteralPath $existingExe) {
        Start-Process -FilePath $existingExe -WorkingDirectory $Target
    }
}
""";
}
