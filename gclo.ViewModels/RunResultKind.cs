namespace gclo.ViewModels;

/// <summary>Outcome of the most recent sync run; drives the results InfoBar's severity.</summary>
public enum RunResultKind
{
    /// <summary>No result to show: nothing has run yet, or a new run/load reset it.</summary>
    None,

    /// <summary>The run finished and every repository succeeded.</summary>
    Success,

    /// <summary>The run finished but at least one repository failed.</summary>
    PartialFailure,

    /// <summary>The run was canceled before every repository finished.</summary>
    Canceled,

    /// <summary>The run itself faulted (for example, the target root could not be created).</summary>
    Error,
}
