// NeutrinoOS Phase 8 - npkg: dependency resolution
//
// Resolution runs over every configured repository index plus the
// installed database and produces a dependency-first (topological)
// install plan:
//
//   * A package satisfies a requirement when its name matches, its
//     architecture is "any" or the target architecture, and its version
//     matches the requested constraint (VersionConstraint semantics;
//     "name@1.2.3" from the command line pins an exact version).
//   * The highest matching version wins; an exact-architecture build is
//     preferred over an "any" build at the same version.
//   * Dependencies already satisfied by the installed database are
//     skipped, so installing A that needs B where B is current plans A
//     only.
//
// Errors (all raised as Exception with a printable message): missing
// dependencies, two different versions of the same package required by
// different dependents, and a requirement that conflicts with an
// already-installed version when that package stays installed.

using System;
using System.Collections.Generic;
using NeutrinoOS.Packaging;
using NeutrinoOS.Utils;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>One repository index together with its configuration.</summary>
public sealed class PackageSource
{
    /// <summary>Repository the index was fetched from.</summary>
    public RepoConfig Repo;

    /// <summary>Parsed repository.json contents.</summary>
    public RepositoryIndex Index;

    /// <summary>Creates a source pair.</summary>
    public PackageSource(RepoConfig repo, RepositoryIndex index)
    {
        Repo = repo;
        Index = index;
    }
}

/// <summary>A package chosen for installation, with the repository it came from.</summary>
public sealed class PlanItem
{
    /// <summary>Index entry (name, version, filename, hashes, dependencies).</summary>
    public RepoPackageInfo Info;

    /// <summary>Repository the entry belongs to.</summary>
    public RepoConfig Repo;

    /// <summary>Creates a plan entry.</summary>
    public PlanItem(RepoPackageInfo info, RepoConfig repo)
    {
        Info = info;
        Repo = repo;
    }
}

/// <summary>Dependency resolver (see file header).</summary>
public sealed class Resolver
{
    private readonly List<PackageSource> _sources;
    private readonly InstalledDatabase _db;
    private readonly bool _replaceInstalled;
    private bool _failed;

    /// <summary>
    /// Creates a resolver; installed packages always win (a version that
    /// does not satisfy a requirement is an error).
    /// </summary>
    public Resolver(List<PackageSource> sources, InstalledDatabase db)
        : this(sources, db, false)
    {
    }

    /// <summary>
    /// Creates a resolver. With <paramref name="replaceInstalled"/> the
    /// installed version may be replaced by a matching repository version
    /// (used by install --force and by upgrade).
    /// </summary>
    public Resolver(List<PackageSource> sources, InstalledDatabase db, bool replaceInstalled)
    {
        _sources = sources == null ? new List<PackageSource>() : sources;
        _db = db;
        _replaceInstalled = replaceInstalled;
    }

    /// <summary>
    /// Failure text of the most recent Resolve call (null on success).
    /// Resolution reports failures through this property instead of
    /// exceptions: exception unwinding across JIT-compiled frames is not
    /// reliable on the Tier-0 JIT (the same reason RepoClient exposes
    /// TryFetchIndex), so an unresolvable request must not throw.
    /// </summary>
    public string LastError { get; private set; }

    /// <summary>
    /// Builds the install plan for one package request. A plan entry is
    /// returned for every package that still needs installing, in
    /// dependency-first order; an already-satisfied request yields an
    /// empty plan. Returns null (with <see cref="LastError"/> set) when
    /// the request cannot be satisfied - conflicts, missing packages,
    /// unsatisfied installed versions and dependency cycles.
    /// </summary>
    public List<PlanItem> Resolve(string name, string requestedVersion)
    {
        LastError = null;
        _failed = false;
        var plan = new List<PlanItem>();
        var chosen = new Dictionary<string, int>();
        var inProgress = new HashSet<string>();
        Visit(name, null, requestedVersion, null, plan, chosen, inProgress);
        return _failed ? null : plan;
    }

    /// <summary>Records a resolution failure and stops the plan walk.</summary>
    private void Fail(string message)
    {
        if (!_failed)
        {
            _failed = true;
            LastError = message;
        }
    }

    /// <summary>
    /// Finds the highest-version candidate for a requirement across all
    /// sources (exact architecture beats "any", then version); null when
    /// nothing matches. Pass null constraint/exactVersion to accept any
    /// version (used by upgrade lookups).
    /// </summary>
    public PlanItem FindBest(string name, string constraintText, string exactVersion)
    {
        PlanItem best = null;
        bool bestExactArch = false;

        for (int s = 0; s < _sources.Count; s++)
        {
            RepositoryIndex index = _sources[s].Index;
            if (index == null || index.Packages == null)
                continue;

            foreach (RepoPackageInfo pkg in index.Packages)
            {
                if (pkg == null || pkg.Name != name)
                    continue;
                if (!ArchCompatible(pkg.Architecture))
                    continue;
                if (!VersionMatches(pkg.Version, constraintText, exactVersion))
                    continue;

                bool exactArch = pkg.Architecture == NpkgPaths.TargetArchitecture;
                bool better = best == null;
                if (!better)
                {
                    if (exactArch != bestExactArch)
                        better = exactArch;
                    else
                        better = CompareSemVer(pkg.Version, best.Info.Version) > 0;
                }
                if (better)
                {
                    best = new PlanItem(pkg, _sources[s].Repo);
                    bestExactArch = exactArch;
                }
            }
        }
        return best;
    }

    /// <summary>True when a package architecture runs on this system.</summary>
    public static bool ArchCompatible(string architecture)
    {
        if (architecture == null || architecture.Length == 0)
            return true;
        return architecture == NpkgPaths.AnyArchitecture
            || architecture == NpkgPaths.TargetArchitecture;
    }

    /// <summary>
    /// True when an index entry version satisfies the requirement:
    /// exactVersion (from "name@x.y.z") pins equality, otherwise the
    /// constraint text is parsed with VersionConstraint (empty or "*"
    /// matches anything; an unparsable constraint falls back to
    /// exact-version equality).
    /// </summary>
    public static bool VersionMatches(SemVersion version, string constraintText, string exactVersion)
    {
        if (exactVersion != null && exactVersion.Length > 0)
        {
            SemVersion exact;
            if (SemVersion.TryParse(exactVersion, out exact))
                return version.CompareTo(exact) == 0;
            return false;
        }
        if (constraintText == null || constraintText.Length == 0 || constraintText == "*")
            return true;

        VersionConstraint constraint;
        if (VersionConstraint.TryParse(constraintText, out constraint))
            return constraint.Matches(version);

        SemVersion fallback;
        if (SemVersion.TryParse(constraintText, out fallback))
            return version.CompareTo(fallback) == 0;
        return false;
    }

    /// <summary>
    /// True when a stored version text (installed database, empty or
    /// unparsable versions included) satisfies the requirement.
    /// </summary>
    public static bool VersionTextMatches(string versionText, string constraintText, string exactVersion)
    {
        SemVersion version;
        if (SemVersion.TryParse(versionText, out version))
            return VersionMatches(version, constraintText, exactVersion);
        return constraintText == null || constraintText.Length == 0 || constraintText == "*";
    }

    /// <summary>Semantic comparison of two index entry versions.</summary>
    public static int CompareSemVer(SemVersion left, SemVersion right)
    {
        return left.CompareTo(right);
    }

    /// <summary>
    /// Comparison of an index entry version against stored version text
    /// (an unparsable stored version falls back to ordinal comparison).
    /// </summary>
    public static int CompareSemVer(SemVersion left, string rightText)
    {
        SemVersion right;
        if (SemVersion.TryParse(rightText, out right))
            return left.CompareTo(right);
        return Util.Compare(left.ToString(), rightText);
    }

    private void Visit(string name, string constraintText, string exactVersion, string requester,
        List<PlanItem> plan, Dictionary<string, int> chosen, HashSet<string> inProgress)
    {
        if (_failed)
            return;

        InstalledPackage installed = _db == null ? null : _db.Find(name);
        if (installed != null && VersionTextMatches(installed.Version, constraintText, exactVersion))
            return;     // dependency already satisfied by the installed database

        int existingIndex;
        if (chosen.TryGetValue(name, out existingIndex))
        {
            PlanItem chosenItem = plan[existingIndex];
            if (!VersionMatches(chosenItem.Info.Version, constraintText, exactVersion))
            {
                Fail("conflicting version requirements for " + name + ": "
                    + chosenItem.Info.Version.ToString() + " already selected, "
                    + Describe(constraintText, exactVersion) + ByWhom(requester) + " needs another version");
            }
            return;
        }

        PlanItem candidate = FindBest(name, constraintText, exactVersion);
        if (candidate == null)
        {
            if (installed != null)
            {
                Fail(name + " " + installed.Version + " is installed but "
                    + Describe(constraintText, exactVersion) + " is required" + ByWhom(requester));
            }
            else
            {
                Fail("package not found in any repository: " + name
                    + (exactVersion != null && exactVersion.Length > 0 ? " (version " + exactVersion + ")" : "")
                    + ByWhom(requester));
            }
            return;
        }

        if (installed != null && !_replaceInstalled)
        {
            Fail(name + " " + installed.Version + " is already installed and does not satisfy "
                + Describe(constraintText, exactVersion) + ByWhom(requester)
                + " (run 'npkg upgrade " + name + "' or use --force)");
            return;
        }

        if (inProgress.Contains(name))
        {
            Fail("dependency cycle detected at " + name);
            return;
        }

        inProgress.Add(name);
        if (candidate.Info.Dependencies != null)
        {
            foreach (KeyValuePair<string, string> dep in candidate.Info.Dependencies)
            {
                Visit(dep.Key, dep.Value, null, candidate.Info.Name, plan, chosen, inProgress);
                if (_failed)
                    return;
            }
        }
        inProgress.Remove(name);

        chosen[name] = plan.Count;
        plan.Add(candidate);
    }

    private static string ByWhom(string requester)
    {
        return requester == null ? "" : " (required by " + requester + ")";
    }

    private static string Describe(string constraintText, string exactVersion)
    {
        if (exactVersion != null && exactVersion.Length > 0)
            return "version " + exactVersion;
        if (constraintText == null || constraintText.Length == 0 || constraintText == "*")
            return "any version";
        return "constraint " + constraintText;
    }
}
