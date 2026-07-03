# gclo.Engine

The core engine of [gclo](https://github.com/KofTwentyTwo/gclo) (Git Clone Large
Organizations). It lists every repository in a GitHub organization or user account
and clones or fast-forward-pulls all of them in parallel, using LibGit2Sharp for
git transport and Octokit for the GitHub API. On Windows it validates every tree
path before checkout and offers structured rename/skip recovery for paths that
git allows but Windows cannot create.

Used by the gclo WinUI app and the cross-platform gclo CLI.
