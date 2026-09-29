using System.Diagnostics.CodeAnalysis;

// Microsoft.WindowsAppSDK injects UndockedRegFreeWinRT-AutoInitializer.cs into the
// compilation from inside the NuGet package (GenerateUndockedRegFreeWinRTCS target).
// The file is marked auto-generated, lives outside the repo so .editorconfig cannot
// reach it, is documented "DO NOT MODIFY", and the SDK exposes no opt-out property.
// Suppressing CA5392 on that single method keeps the rule fully active for every
// P/Invoke we write ourselves, which a blanket NoWarn would not.
[assembly: SuppressMessage(
    "Security",
    "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes",
    Scope = "member",
    Target = "M:Microsoft.Windows.Foundation.UndockedRegFreeWinRTCS.NativeMethods.WindowsAppRuntime_EnsureIsLoaded",
    Justification = "Declared in an SDK-injected file we do not own and must not modify.")]

// CA1515 assumes an application's types are never referenced from outside its assembly. App has to
// stay public regardless: the XAML compiler generates its other partial declaration as public, and
// C# requires every partial declaration to agree. Scoped to the one type rather than suppressed
// project-wide so CA1515 keeps firing on any other type that becomes public by accident.
[assembly: SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Scope = "type",
    Target = "~T:Dongled.App.App",
    Justification = "Must be public: the XAML compiler generates App's other partial declaration as public.")]
