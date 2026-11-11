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



   /// <summary>
   /// The release asset the manifests install: releases up to 1.0.0 shipped it as
   /// gclo-cli-win-x64.zip, the shared release workflow names it with the version.
   /// </summary>
   private static string CliZipUrlPattern(string version)
   {
      string v = Regex.Escape(version);
      return $@"^https://git\.kof22\.com/KofTwentyTwo/gclo/releases/download/v{v}/gclo-cli-win-x64(-{v})?\.zip$";
   }



   private static (string Version, string Url, string Hash) ReadScoop()
   {
      using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "bucket", "gclo.json")));
      JsonElement root = doc.RootElement;
      JsonElement x64 = root.GetProperty("architecture").GetProperty("64bit");
      Assert.Equal("gclo.exe", root.GetProperty("bin").GetString());
      Assert.Equal("MIT", root.GetProperty("license").GetString());
      /////////////////////////////////////////////////////////////////////////////
      // Everything is asked of the project's own server (ADR 0009): the newest  //
      // version (the tag of its latest release, 'v1.2.3' read as '1.2.3'), the  //
      // download of a later version, its hash, and the pages the notes name.    //
      /////////////////////////////////////////////////////////////////////////////
      JsonElement checkver = root.GetProperty("checkver");
      Assert.Equal("https://git.kof22.com/api/v1/repos/KofTwentyTwo/gclo/releases/latest", checkver.GetProperty("url").GetString());
      Assert.Equal("$.tag_name", checkver.GetProperty("jsonpath").GetString());
      Assert.Equal(@"v([\d.]+)", checkver.GetProperty("regex").GetString());
      Assert.Equal("https://git.kof22.com/KofTwentyTwo/gclo", root.GetProperty("homepage").GetString());
      JsonElement autoupdate = root.GetProperty("autoupdate").GetProperty("architecture").GetProperty("64bit");
      Assert.Equal(
          "https://git.kof22.com/KofTwentyTwo/gclo/releases/download/v$version/gclo-cli-win-x64-$version.zip",
          autoupdate.GetProperty("url").GetString());
      Assert.Equal("$baseurl/SHA256SUMS", autoupdate.GetProperty("hash").GetProperty("url").GetString());
      Assert.All(
          root.GetProperty("notes").EnumerateArray().Select(note => note.GetString()!),
          note => Assert.DoesNotContain("github.com/KofTwentyTwo", note, StringComparison.Ordinal));
      return (root.GetProperty("version").GetString()!, x64.GetProperty("url").GetString()!, x64.GetProperty("hash").GetString()!);
   }



   private static (string Version, string Url, string Hash) ReadChocolatey()
   {
      var nuspec = XDocument.Load(Path.Combine(RepoRoot, "packaging", "chocolatey", "gclo.nuspec"));
      XNamespace ns = nuspec.Root!.Name.Namespace;
      XElement metadata = nuspec.Root.Element(ns + "metadata")!;
      Assert.Equal("gclo", metadata.Element(ns + "id")!.Value);
      string version = metadata.Element(ns + "version")!.Value;
      Assert.Equal($"https://git.kof22.com/KofTwentyTwo/gclo/releases/tag/v{version}", metadata.Element(ns + "releaseNotes")!.Value);
      Assert.DoesNotContain("github.com/KofTwentyTwo", nuspec.ToString(), StringComparison.OrdinalIgnoreCase);
      Assert.DoesNotContain("githubusercontent.com", nuspec.ToString(), StringComparison.OrdinalIgnoreCase);

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
      Assert.Matches(CliZipUrlPattern(version), url);
      Assert.Matches(s_sha256, hash);
   }



   [Fact]
   public void ChocolateyPackage_IsValid_AndPointsAtAStableCliRelease()
   {
      (string? version, string? url, string? hash) = ReadChocolatey();

      Assert.Matches(@"^\d+\.\d+\.\d+$", version);
      Assert.Matches(CliZipUrlPattern(version), url);
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
