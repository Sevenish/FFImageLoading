﻿using Android.Widget;
using System;
using System.ComponentModel;
using Xamarin.Forms;
using Xamarin.Forms.Platform.Android;
using FFImageLoading.Work;
using FFImageLoading.Forms.Droid;
using FFImageLoading.Forms;
using Android.Runtime;
using Android.Graphics.Drawables;
using Android.Graphics;
using System.IO;
using System.Threading.Tasks;
using FFImageLoading.Forms.Args;
using FFImageLoading.Helpers;
using FFImageLoading.Views;
using Android.Views;
using System.Reflection;

//[assembly: ExportRenderer(typeof(CachedImage), typeof(CachedImageRenderer))]
using System.Linq;
namespace FFImageLoading.Forms.Droid
{
    /// <summary>
    /// CachedImage Implementation
    /// </summary>
    [Preserve(AllMembers=true)]
    public class CachedImageRenderer : ViewRenderer<CachedImage, CachedImageView>
    {
        /// <summary>
        ///   Used for registration with dependency service
        /// </summary>
        public static void Init(bool? enableFastRenderer = default(bool?))
        {
#pragma warning disable 0219
            var ignore1 = typeof(CachedImageRenderer);
            var ignore2 = typeof(CachedImageFastRenderer);
            var ignore3 = typeof(CachedImage);
#pragma warning restore 0219

            var enabled = false;

            if (enableFastRenderer.HasValue)
            {
                enabled = enableFastRenderer.Value;
            }
            else
            {
                enabled = CachedImageFastRenderer.ElementRendererType != null;
            }

            RegisterRenderer(typeof(CachedImage), enabled ? typeof(CachedImageFastRenderer) : typeof(CachedImageRenderer));
        }

        static void RegisterRenderer(Type type, Type renderer)
        {
            var assembly = typeof(Xamarin.Forms.Image).Assembly;
            Type registrarType = assembly.GetType("Xamarin.Forms.Internals.Registrar") ?? assembly.GetType("Xamarin.Forms.Registrar");
            var registrarProperty = registrarType.GetProperty("Registered", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            var registrar = registrarProperty.GetValue(registrarType, null);
            var registerMethod = registrar.GetType().GetRuntimeMethod("Register", new[] { typeof(Type), typeof(Type) });
            registerMethod.Invoke(registrar, new[] { type, renderer });
        }

        bool _isDisposed;
        IScheduledWork _currentTask;
        ImageSourceBinding _lastImageSource;
        readonly MotionEventHelper _motionEventHelper = CachedImage.FixedAndroidMotionEventHandler ? new MotionEventHelper() : null;
        readonly static Type _platformDefaultRendererType = typeof(ImageRenderer).Assembly.GetType("Xamarin.Forms.Platform.Android.Platform+DefaultRenderer");
        readonly static MethodInfo _platformDefaultRendererTypeNotifyFakeHandling = _platformDefaultRendererType?.GetMethod("NotifyFakeHandling", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public CachedImageRenderer()
        {
            AutoPackage = false;
        }

        public CachedImageRenderer(IntPtr javaReference, JniHandleOwnership transfer) : this()
        {
            AutoPackage = false;
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            if (base.OnTouchEvent(e))
                return true;

            return CachedImage.FixedAndroidMotionEventHandler ? _motionEventHelper.HandleMotionEvent(Parent, e) : false;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                CancelIfNeeded();
            }

            base.Dispose(disposing);
        }

        protected override void OnElementChanged(ElementChangedEventArgs<CachedImage> e)
        {
            base.OnElementChanged(e);

            if (Control == null && Element != null && !_isDisposed)
            {
                var nativeControl = new CachedImageView(Context);
                SetNativeControl(nativeControl);
            }

            if (e.OldElement != null)
            {
                e.OldElement.InternalReloadImage = null;
                e.OldElement.InternalCancel = null;
                e.OldElement.InternalGetImageAsJPG = null;
                e.OldElement.InternalGetImageAsPNG = null;
            }

            if (e.NewElement != null)
            {
                e.NewElement.InternalReloadImage = new Action(ReloadImage);
                e.NewElement.InternalCancel = new Action(CancelIfNeeded);
                e.NewElement.InternalGetImageAsJPG = new Func<GetImageAsJpgArgs, Task<byte[]>>(GetImageAsJpgAsync);
                e.NewElement.InternalGetImageAsPNG = new Func<GetImageAsPngArgs, Task<byte[]>>(GetImageAsPngAsync);

                _motionEventHelper?.UpdateElement(e.NewElement);
                UpdateBitmap(Control, Element, e.OldElement);
                UpdateAspect();
            }
        }

        protected override void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            base.OnElementPropertyChanged(sender, e);

            if (e.PropertyName == CachedImage.SourceProperty.PropertyName)
            {
                UpdateBitmap(Control, Element, null);
            }
            if (e.PropertyName == CachedImage.AspectProperty.PropertyName)
            {
                UpdateAspect();
            }
        }

        protected override CachedImageView CreateNativeControl()
        {
            return new CachedImageView(Context);
        }

        void UpdateAspect()
        {
            if (Control == null || Control.Handle == IntPtr.Zero || Element == null || _isDisposed)
                return;

            if (Element.Aspect == Aspect.AspectFill)
                Control.SetScaleType(ImageView.ScaleType.CenterCrop);

            else if (Element.Aspect == Aspect.Fill)
                Control.SetScaleType(ImageView.ScaleType.FitXy);

            else
                Control.SetScaleType(ImageView.ScaleType.FitCenter);
        }

        void UpdateBitmap(CachedImageView imageView, CachedImage image, CachedImage previousImage)
        {
            CancelIfNeeded();

            if (image == null || imageView == null || imageView.Handle == IntPtr.Zero || _isDisposed)
                return;

            var ffSource = ImageSourceBinding.GetImageSourceBinding(image.Source, image);
            if (ffSource == null)
            {
                if (_lastImageSource == null)
                    return;

                _lastImageSource = null;
                imageView.SetImageResource(global::Android.Resource.Color.Transparent);
                return;
            }

            if (previousImage != null && !ffSource.Equals(_lastImageSource))
            {
                _lastImageSource = null;
                imageView.SkipInvalidate();
                Control.SetImageResource(global::Android.Resource.Color.Transparent);
            }

            image.SetIsLoading(true);

            var placeholderSource = ImageSourceBinding.GetImageSourceBinding(image.LoadingPlaceholder, image);
            var errorPlaceholderSource = ImageSourceBinding.GetImageSourceBinding(image.ErrorPlaceholder, image);
            TaskParameter imageLoader;
            image.SetupOnBeforeImageLoading(out imageLoader, ffSource, placeholderSource, errorPlaceholderSource);

            if (imageLoader != null)
            {
                var finishAction = imageLoader.OnFinish;
                var sucessAction = imageLoader.OnSuccess;

                imageLoader.Finish((work) =>
                {
                    finishAction?.Invoke(work);
                    ImageLoadingFinished(image);
                });

                imageLoader.Success((imageInformation, loadingResult) =>
                {
                    sucessAction?.Invoke(imageInformation, loadingResult);
                    _lastImageSource = ffSource;
                });

                _currentTask = imageLoader.Into(imageView);
            }
        }

        async void ImageLoadingFinished(CachedImage element)
        {
            await ImageService.Instance.Config.MainThreadDispatcher.PostAsync(() =>
            {
                if (element != null && !_isDisposed)
                {
                    ((IVisualElementController)element).NativeSizeChanged();
                    element.SetIsLoading(false);
                }
            });
        }

        void ReloadImage()
        {
            UpdateBitmap(Control, Element, null);
        }

        void CancelIfNeeded()
        {
            try
            {
                var taskToCancel = _currentTask;
                if (taskToCancel != null && !taskToCancel.IsCancelled)
                {
                    taskToCancel.Cancel();
                }
            }
            catch (Exception) { }
        }

        Task<byte[]> GetImageAsJpgAsync(GetImageAsJpgArgs args)
        {
            return GetImageAsByteAsync(Bitmap.CompressFormat.Jpeg, args.Quality, args.DesiredWidth, args.DesiredHeight);
        }

        Task<byte[]> GetImageAsPngAsync(GetImageAsPngArgs args)
        {
            return GetImageAsByteAsync(Bitmap.CompressFormat.Png, 90, args.DesiredWidth, args.DesiredHeight);
        }

        async Task<byte[]> GetImageAsByteAsync(Bitmap.CompressFormat format, int quality, int desiredWidth, int desiredHeight)
        {
            if (Control == null)
                return null;

            var drawable = Control.Drawable as BitmapDrawable;

            if (drawable == null || drawable.Bitmap == null)
                return null;

            Bitmap bitmap = drawable.Bitmap;

            if (desiredWidth != 0 || desiredHeight != 0)
            {
                double widthRatio = (double)desiredWidth / (double)bitmap.Width;
                double heightRatio = (double)desiredHeight / (double)bitmap.Height;

                double scaleRatio = Math.Min(widthRatio, heightRatio);

                if (desiredWidth == 0)
                    scaleRatio = heightRatio;

                if (desiredHeight == 0)
                    scaleRatio = widthRatio;

                int aspectWidth = (int)((double)bitmap.Width * scaleRatio);
                int aspectHeight = (int)((double)bitmap.Height * scaleRatio);

                bitmap = Bitmap.CreateScaledBitmap(bitmap, aspectWidth, aspectHeight, true);
            }

            using (var stream = new MemoryStream())
            {
                await bitmap.CompressAsync(format, quality, stream).ConfigureAwait(false);
                var compressed = stream.ToArray();

                if (desiredWidth != 0 || desiredHeight != 0)
                {
                    bitmap.Recycle();
                    bitmap.TryDispose();
                }

                return compressed;
            }
        }

        internal class MotionEventHelper
        {
            VisualElement _element;
            bool _isInViewCell;

            public bool HandleMotionEvent(IViewParent parent, MotionEvent motionEvent)
            {
                if (_isInViewCell || _element.InputTransparent || motionEvent.Action == MotionEventActions.Cancel)
                {
                    return false;
                }

                var rendererType = parent.GetType();
                if (!_platformDefaultRendererType.IsAssignableFrom(rendererType))
                {
                    return false;
                }

                try
                {
                    // Let the container know that we're "fake" handling this event
                    if (_platformDefaultRendererTypeNotifyFakeHandling != null)
                    {
                        _platformDefaultRendererTypeNotifyFakeHandling.Invoke(parent, null);
                        return true;
                    }
                }
                catch (Exception) { }

                return false;
            }

            public void UpdateElement(VisualElement element)
            {
                _isInViewCell = false;
                _element = element;

                if (_element == null)
                {
                    return;
                }

                // Determine whether this control is inside a ViewCell;
                // we don't fake handle the events because ListView needs them for row selection
                _isInViewCell = IsInViewCell(element);
            }

            static bool IsInViewCell(VisualElement element)
            {
                var parent = element.Parent;
                while (parent != null)
                {
                    if (parent is ViewCell)
                    {
                        return true;
                    }
                    parent = parent.Parent;
                }

                return false;
            }
        }
    }
}

