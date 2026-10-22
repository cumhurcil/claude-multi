using System;
using System.Threading.Tasks;
using ClaudeMulti.Core;

namespace ClaudeMulti.App.ViewModels;

/// <summary>Questions the view models have to ask the user; implemented by the window. Dialogs are asynchronous on every system.</summary>
public interface IDialogs
{
    Task<bool> Confirm(string title, string message);
    Task Alert(string title, string message);
    Task Inform(string title, string message);
    Task<string?> Prompt(string title, string label, string initial, Func<string, string?> validate);

    /// <summary>Asks where to save a file; <paramref name="extension"/> is like "cmxfer". Null when cancelled.</summary>
    Task<string?> SaveFile(string title, string description, string extension, string fileName);

    /// <summary>Asks for an existing file; null when cancelled.</summary>
    Task<string?> OpenFile(string title, string description, string extension);

    /// <summary>Asks for a gateway address, API key and models; null when cancelled.</summary>
    Task<ApiSettings?> EditApi(string title, ApiSettings current);
}
