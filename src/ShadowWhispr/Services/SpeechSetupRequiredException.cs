namespace ShadowWhispr.Services;

/// <summary>
/// Thrown when the local speech environment (.venv) has not been created yet.
/// Carries the path to the one-time setup script so the UI can offer to run it.
/// </summary>
public sealed class SpeechSetupRequiredException : Exception
{
    public SpeechSetupRequiredException(string setupScriptPath)
        : base("The local speech engine is not set up yet.")
    {
        SetupScriptPath = setupScriptPath;
    }

    public string SetupScriptPath { get; }
}
