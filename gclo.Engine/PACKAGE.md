# gclo.Engine

The core engine of [gclo](https://git.kof22.com/KofTwentyTwo/gclo) (Git Clone Large
Organizations). It lists every repository in a GitHub organization or user account,
or in one on a Gitea server, and clones or fast-forward-pulls all of them in
parallel, using LibGit2Sharp for git transport, Octokit for the GitHub API, and
plain HTTPS for the Gitea API. On Windows it validates every tree
path before checkout and offers structured rename/skip recovery for paths that
git allows but Windows cannot create.

Used by the gclo WinUI app and the cross-platform gclo CLI.
