using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;

namespace NetworkMonitor.Processor;

[Activity(
    Theme = "@style/Maui.MainTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize |
                           ConfigChanges.Orientation |
                           ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTask)]
public class MainActivity : AppCompatActivity, View.IOnApplyWindowInsetsListener
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (!OperatingSystem.IsAndroidVersionAtLeast(21))
        {
            return;
        }

        EdgeToEdge.Enable(this);

        var window = Window;
        if (window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(28))
        {
            var layoutParams = window.Attributes;
            if (layoutParams is not null)
            {
                layoutParams.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                window.Attributes = layoutParams;
            }
        }

        // Keep adjust resize behavior so onscreen keyboard shifts content.
        window.SetSoftInputMode(SoftInput.AdjustResize);

        var decorView = window.DecorView;
        if (decorView is null)
        {
            return;
        }

        decorView.SetOnApplyWindowInsetsListener(this);
        decorView.RequestApplyInsets();
    }

    public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
    {
        var compat = WindowInsetsCompat.ToWindowInsetsCompat(insets, v);
        var systemBars = compat.GetInsets(WindowInsetsCompat.Type.SystemBars());

        var window = Window;
        var decorView = window?.DecorView;

        var content = decorView?.FindViewById(Android.Resource.Id.Content);
        if (content is ViewGroup root && root.ChildCount > 0)
        {
            var child = root.GetChildAt(0);
            if (child is not null)
            {
                child.SetPadding(systemBars.Left, systemBars.Top, systemBars.Right, systemBars.Bottom);
            }
        }

        return insets;
    }
}
