/*
 * Copyright (c) 2026 James Maes (KofTwentyTwo)
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using gclo.ViewModels;


namespace gclo.Engine.Tests;


/// <summary>
/// <see cref="WindowClaim"/>: one window per data folder (#216). The first claim
/// on a folder is the only one, a second is not and can ask the first to come to
/// the front, another folder is another claim, and a claim that is given up is free.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowClaimTests : IDisposable
{
   private readonly string _root = Path.Combine(Path.GetTempPath(), "gclo-tests", Guid.NewGuid().ToString("N"));



   public void Dispose() => GitTestHelpers.TryDeleteDirectory(_root);



   [Fact]
   public void TheFirstClaimOnAFolder_IsTheOnlyOne_AndASecondIsNot_UntilTheFirstIsGivenUp()
   {
      string folder = Path.Combine(_root, "data");

      using(var first = WindowClaim.For(folder))
      {
         Assert.True(first.IsTheOnlyOne);
         Assert.True(File.Exists(Path.Combine(folder, "window.lock"))); // the folder was made for it

         // The same folder, spelled another way: the same claim.
         using(var second = WindowClaim.For(folder.ToUpperInvariant() + Path.DirectorySeparatorChar))
         {
            Assert.False(second.IsTheOnlyOne);
         }

         // A second that came and went does not free the first's claim.
         using var third = WindowClaim.For(folder);
         Assert.False(third.IsTheOnlyOne);

         // Another folder is another claim.
         using var elsewhere = WindowClaim.For(Path.Combine(_root, "other"));
         Assert.True(elsewhere.IsTheOnlyOne);
      }

      // Given up: the file is gone, and the next start is the only one again.
      Assert.False(File.Exists(Path.Combine(folder, "window.lock")));
      using var next = WindowClaim.For(folder);
      Assert.True(next.IsTheOnlyOne);
   }



   [Fact]
   public void ASecondStart_AsksTheWindowThatHoldsTheFolder_ToComeToTheFront()
   {
      string folder = Path.Combine(_root, "data");
      using var first = WindowClaim.For(folder);
      using var replaced = new ManualResetEventSlim();
      using var asked = new SemaphoreSlim(0);
      first.OnAskedToShow(replaced.Set);
      first.OnAskedToShow(() => asked.Release()); // set again: the one before is not called any more

      using(var second = WindowClaim.For(folder))
      {
         Assert.False(second.IsTheOnlyOne);
         second.AskTheOtherToShowItself();
      }

      Assert.True(asked.Wait(TimeSpan.FromSeconds(10)), "the first window was not asked to show itself");

      // Once for each request.
      using(var again = WindowClaim.For(folder))
      {
         again.AskTheOtherToShowItself();
      }

      Assert.True(asked.Wait(TimeSpan.FromSeconds(10)), "the first window was not asked a second time");
      Assert.False(replaced.IsSet);
   }



   [Fact]
   public void ARequestForAnotherFolder_DoesNotReachThisWindow()
   {
      using var here = WindowClaim.For(Path.Combine(_root, "data"));
      using var asked = new ManualResetEventSlim();
      here.OnAskedToShow(asked.Set);

      using(var elsewhere = WindowClaim.For(Path.Combine(_root, "other")))
      {
         elsewhere.AskTheOtherToShowItself();
      }

      Assert.False(asked.Wait(TimeSpan.FromMilliseconds(300)));
   }



   [Fact]
   public void AFolderThatCannotBeWrittenTo_HoldsNoClaim_AndStopsNoWindow()
   {
      // The data folder's path is taken by a file: no folder can be made there, and no lock file in it.
      Directory.CreateDirectory(_root);
      string blocked = Path.Combine(_root, "a-file");
      File.WriteAllText(blocked, "not a folder");

      using var first = WindowClaim.For(blocked);
      using var second = WindowClaim.For(blocked);

      Assert.True(first.IsTheOnlyOne);
      Assert.True(second.IsTheOnlyOne);
   }



   [Fact]
   public void TheKeyOfAFolder_IsTheSameForEverySpellingOfIt_AndAnotherForAnotherFolder()
   {
      string folder = Path.Combine(_root, "Data");
      string key = WindowClaim.KeyFor(folder);

      Assert.Equal(32, key.Length);
      Assert.Matches("^[0-9A-F]{32}$", key);
      Assert.Equal(key, WindowClaim.KeyFor(folder.ToLowerInvariant()), StringComparer.Ordinal);
      Assert.Equal(key, WindowClaim.KeyFor(folder + Path.DirectorySeparatorChar), StringComparer.Ordinal);
      Assert.Equal(key, WindowClaim.KeyFor(Path.Combine(_root, "other", "..", "Data")), StringComparer.Ordinal);
      Assert.NotEqual(key, WindowClaim.KeyFor(Path.Combine(_root, "Data2")), StringComparer.Ordinal);
   }



   [Fact]
   public void AClaim_NeedsAFolder_AndSomethingToDoWhenAsked()
   {
      Assert.Throws<ArgumentException>(() => WindowClaim.For(" "));
      Assert.Throws<ArgumentNullException>(() => WindowClaim.For(null!));
      Assert.Throws<ArgumentException>(() => WindowClaim.KeyFor(""));

      using var claim = WindowClaim.For(Path.Combine(_root, "data"));
      Assert.Throws<ArgumentNullException>(() => claim.OnAskedToShow(null!));

      // Given up twice is given up.
      claim.Dispose();
   }
}
