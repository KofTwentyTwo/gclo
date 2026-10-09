/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;


namespace gclo.Cli.Tests;


/// <summary>
/// Pins the package-manager manifests the repository ships (#9): the Scoop bucket
/// manifest and the Chocolatey package must stay valid, point at the same CLI
/// asset of one stable release, and agree with each other — the release workflow
/// rewrites them, and a drift would break `scoop install` / `choco install`.
/// </summary>
public sealed class PackagingManifestTests
{
   private static readonly Regex s_sha256 = new("^[0-9a-f]{64}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));



   private static string RepoRoot
   {
      get
      {
         DirectoryInfo? current = new(AppContext.BaseDirectory);
         while(current is not null && !File.Exists(Path.Combine(current.FullName, "gclo.slnx")))
         {
            current = current.Parent;
         }
         return current?.FullName ?? throw new FileNotFoundException("gclo.slnx not found above the test directory");
      }
   }



   private static (string Version, string Url, string Hash) ReadScoop()
   {
      using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "bucket", "gclo.json")));
      JsonElement root = doc.RootElement;
      JsonElement x64 = root.GetProperty("architecture").GetProperty("64bit");
      Assert.Equal("gclo.exe", root.GetProperty("bin").GetString());
      Assert.Equal("MIT", root.GetProperty("license").GetString());
      Assert.Equal("github", root.GetProperty("checkver").GetString());
      string autoupdateUrl = root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit").GetProperty("url").GetString()!;
      Assert.Contains("v$version/gclo-cli-win-x64.zip", autoupdateUrl);
      Assert.EndsWith("/SHA256SUMS", root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit").GetProperty("hash").GetProperty("url").GetString());
      return (root.GetProperty("version").GetString()!, x64.GetProperty("url").GetString()!, x64.GetProperty("hash").GetString()!);
   }



   private static (string Version, string Url, string Hash) ReadChocolatey()
   {
      var nuspec = XDocument.Load(Path.Combine(RepoRoot, "packaging", "chocolatey", "gclo.nuspec"));
      XNamespace ns = nuspec.Root!.Name.Namespace;
      XElement metadata = nuspec.Root.Element(ns + "metadata")!;
      Assert.Equal("gclo", metadata.Element(ns + "id")!.Value);
      string version = metadata.Element(ns + "version")!.Value;
      Assert.EndsWith($"/releases/tag/v{version}", metadata.Element(ns + "releaseNotes")!.Value);

      string install = File.ReadAllText(Path.Combine(RepoRoot, "packaging", "chocolatey", "tools", "chocolateyinstall.ps1"));
      string url = Regex.Match(install, @"url64bit\s*=\s*'(?<value>[^']+)'", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["value"].Value;
      string hash = Regex.Match(install, @"checksum64\s*=\s*'(?<value>[^']+)'", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["value"].Value;
      Assert.Contains("Install-ChocolateyZipPackage", install);
      return (version, url, hash);
   }



   [Fact]
   public void ScoopManifest_IsValid_AndPointsAtAStableCliRelease()
   {
      (string? version, string? url, string? hash) = ReadScoop();

      Assert.Matches(@"^\d+\.\d+\.\d+$", version); // package managers get stable releases only
      Assert.Equal($"https://github.com/KofTwentyTwo/gclo/releases/download/v{version}/gclo-cli-win-x64.zip", url);
      Assert.Matches(s_sha256, hash);
   }



   [Fact]
   public void ChocolateyPackage_IsValid_AndPointsAtAStableCliRelease()
   {
      (string? version, string? url, string? hash) = ReadChocolatey();

      Assert.Matches(@"^\d+\.\d+\.\d+$", version);
      Assert.Equal($"https://github.com/KofTwentyTwo/gclo/releases/download/v{version}/gclo-cli-win-x64.zip", url);
      Assert.Matches(s_sha256, hash);
   }



   [Fact]
   public void ScoopAndChocolatey_DescribeTheSameRelease()
   {
      (string Version, string Url, string Hash) scoop = ReadScoop();
      (string Version, string Url, string Hash) choco = ReadChocolatey();

      Assert.Equal(scoop.Version, choco.Version);
      Assert.Equal(scoop.Url, choco.Url);
      Assert.Equal(scoop.Hash, choco.Hash);
   }
}
