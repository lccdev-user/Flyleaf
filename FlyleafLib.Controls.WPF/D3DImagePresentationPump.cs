using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FlyleafLib.Controls.WPF;

public static class D3DImagePresentationPump
{
    static readonly object sync = new();
    static readonly HashSet<D3DImageSurface> pendingSurfaces = [];
    static bool isSubscribed;

    internal static void Request(D3DImageSurface surface)
    {
        bool shouldSubscribe = false;

        lock (sync)
        {
            pendingSurfaces.Add(surface);
            if (!isSubscribed)
            {
                isSubscribed = true;
                shouldSubscribe = true;
            }
        }

        if (shouldSubscribe) RunOnUiThread(Subscribe);
    }

    internal static void Remove(D3DImageSurface surface)
    {
        bool shouldUnsubscribe = false;

        lock (sync)
        {
            pendingSurfaces.Remove(surface);
            if (isSubscribed && pendingSurfaces.Count == 0)
            {
                isSubscribed = false;
                shouldUnsubscribe = true;
            }
        }

        if (shouldUnsubscribe) RunOnUiThread(Unsubscribe);
    }

    static void Subscribe()
    {
        lock (sync)
        {
            if (!isSubscribed) return;
        }

        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
    }

    static void Unsubscribe() => CompositionTarget.Rendering -= OnRendering;

    static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Render);
    }

    static void OnRendering(object sender, EventArgs e)
    {
        D3DImageSurface[] surfaces;
        bool shouldUnsubscribe = false;

        lock (sync)
        {
            if (pendingSurfaces.Count == 0)
            {
                if (isSubscribed)
                {
                    isSubscribed = false;
                    shouldUnsubscribe = true;
                }

                surfaces = [];
            }
            else
            {
                surfaces = pendingSurfaces.ToArray();
                pendingSurfaces.Clear();
            }
        }

        if (shouldUnsubscribe)
        {
            Unsubscribe();
            return;
        }

        foreach (var surface in surfaces) surface.ProcessPendingPresentation();

        lock (sync)
        {
            if (pendingSurfaces.Count == 0 && isSubscribed)
            {
                isSubscribed = false;
                shouldUnsubscribe = true;
            }
        }

        if (shouldUnsubscribe) Unsubscribe();
    }
}
