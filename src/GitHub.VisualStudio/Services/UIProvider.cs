﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Controls;
using GitHub.Infrastructure;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using NLog;
using NullGuard;

namespace GitHub.VisualStudio
{
    [Export(typeof(IUIProvider))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class UIProvider : IUIProvider, IDisposable
    {
        class OwnedComposablePart
        {
            public object Owner { get; set; }
            public ComposablePart Part { get; set; }
        }

        static readonly Logger log = LogManager.GetCurrentClassLogger();
        CompositeDisposable disposables = new CompositeDisposable();
        readonly IServiceProvider serviceProvider;
        CompositionContainer tempContainer;
        readonly Dictionary<string, OwnedComposablePart> tempParts;
        ExportLifetimeContext<IUIController> currentUIFlow;
        readonly Version currentVersion;
        bool initializingLogging = false;

        [AllowNull]
        public ExportProvider ExportProvider { get; }

        [AllowNull]
        public IServiceProvider GitServiceProvider { get; set; }

        bool Initialized { get { return ExportProvider != null; } }

        [ImportingConstructor]
        public UIProvider([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider)
        {
            this.currentVersion = typeof(UIProvider).Assembly.GetName().Version;
            this.serviceProvider = serviceProvider;

            var componentModel = serviceProvider.GetService(typeof(SComponentModel)) as IComponentModel;
            Debug.Assert(componentModel != null, "Service of type SComponentModel not found");
            if (componentModel == null)
            {
                log.Error("Service of type SComponentModel not found");
                return;
            }
            ExportProvider = componentModel.DefaultExportProvider;

            if (ExportProvider == null)
            {
                log.Error("DefaultExportProvider could not be obtained.");
                return;
            }

            tempContainer = AddToDisposables(new CompositionContainer(new ComposablePartExportProvider()
            {
                SourceProvider = ExportProvider
            }));
            tempParts = new Dictionary<string, OwnedComposablePart>();
        }

        [return: AllowNull]
        public object TryGetService(Type serviceType)
        {
            if (!Initialized)
                return null;

            if (!initializingLogging && log.Factory.Configuration == null)
            {
                initializingLogging = true;
                try
                {
                    var logging = TryGetService(typeof(ILoggingConfiguration)) as ILoggingConfiguration;
                    logging.Configure();
                }
                catch
                {
                }
            }

            string contract = AttributedModelServices.GetContractName(serviceType);
            var instance = AddToDisposables(tempContainer.GetExportedValueOrDefault<object>(contract));
            if (instance != null)
                return instance;

            instance = AddToDisposables(ExportProvider.GetExportedValues<object>(contract).FirstOrDefault(x => contract.StartsWith("github.", StringComparison.OrdinalIgnoreCase) ? x.GetType().Assembly.GetName().Version == currentVersion : true));

            if (instance != null)
                return instance;

            instance = serviceProvider.GetService(serviceType);
            if (instance != null)
                return instance;

            if (GitServiceProvider != null)
            {
                instance = GitServiceProvider.GetService(serviceType);
                if (instance != null)
                    return instance;
            }

            return null;
        }

        [return: AllowNull]
        public object TryGetService(string typename)
        {
            var type = Type.GetType(typename, false, true);
            return TryGetService(type);
        }

        public object GetService(Type serviceType)
        {
            var instance = TryGetService(serviceType);
            if (instance != null)
                return instance;

            string contract = AttributedModelServices.GetContractName(serviceType);
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                "Could not locate any instances of contract {0}.", contract));
        }

        public T GetService<T>()
        {
            return (T)GetService(typeof(T));
        }

        [return: AllowNull]
        public T TryGetService<T>() where T : class
        {
            return TryGetService(typeof(T)) as T;
        }

        [SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter")]
        public Ret GetService<T, Ret>() where Ret : class
        {
            return GetService<T>() as Ret;
        }

        public void AddService<T>(object owner, T instance)
        {
            AddService(typeof(T), owner, instance);
        }

        public void AddService(Type t, object owner, object instance)
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot add service.");
                return;
            }

            string contract = AttributedModelServices.GetContractName(t);
            Debug.Assert(!string.IsNullOrEmpty(contract), "Every type must have a contract name");

            // we want to remove stale instances of a service, if they exist, regardless of who put them there
            RemoveService(t, null);

            var batch = new CompositionBatch();
            var part = batch.AddExportedValue(contract, instance);
            Debug.Assert(part != null, "Adding an exported value must return a non-null part");
            tempParts.Add(contract, new OwnedComposablePart { Owner = owner, Part = part });
            tempContainer.Compose(batch);
        }

        /// <summary>
        /// Removes a service from the catalog
        /// </summary>
        /// <param name="t">The type we want to remove</param>
        /// <param name="owner">The owner, which either has to match what was passed to AddService,
        /// or if it's null, the service will be removed without checking for ownership</param>
        public void RemoveService(Type t, [AllowNull] object owner)
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot remove service.");
                return;
            }

            string contract = AttributedModelServices.GetContractName(t);
            Debug.Assert(!string.IsNullOrEmpty(contract), "Every type must have a contract name");

            OwnedComposablePart part; 
            if (tempParts.TryGetValue(contract, out part))
            {
                if (owner != null && part.Owner != owner)
                    return;
                tempParts.Remove(contract);
                var batch = new CompositionBatch();
                batch.RemovePart(part.Part);
                tempContainer.Compose(batch);
            }
        }

        UI.WindowController windowController;
        public IObservable<LoadData> SetupUI(UIControllerFlow controllerFlow, [AllowNull] IConnection connection)
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot setup UI.");
                return Observable.Empty<LoadData>();
            }

            StopUI();

            var factory = TryGetService(typeof(IExportFactoryProvider)) as IExportFactoryProvider;
            currentUIFlow = factory.UIControllerFactory.CreateExport();
            var disposable = currentUIFlow;
            var ui = currentUIFlow.Value;
            var creation = ui.SelectFlow(controllerFlow);
            windowController = new UI.WindowController(creation);
            windowController.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            windowController.Closed += StopUIFlowWhenWindowIsClosedByUser;
            creation.Subscribe(c => {}, () =>
            {
                windowController.Closed -= StopUIFlowWhenWindowIsClosedByUser;
                windowController.Close();
                if (currentUIFlow != disposable)
                    StopUI(disposable);
                else
                    StopUI();
            });
            ui.Start(connection);
            return creation;
        }

        public IObservable<bool> ListenToCompletionState()
        {
            var ui = currentUIFlow?.Value;
            if (ui == null)
            {
                log.Error("UIProvider:ListenToCompletionState:Cannot call ListenToCompletionState without calling SetupUI first");
#if DEBUG
                throw new InvalidOperationException("Cannot call ListenToCompletionState without calling SetupUI first");
#endif
            }
            return ui?.ListenToCompletionState() ?? Observable.Return(false);
        }

        public void RunUI()
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot run UI.");
                return;
            }

            Debug.Assert(windowController != null, "WindowController is null, did you forget to call SetupUI?");
            if (windowController == null)
            {
                log.Error("WindowController is null, cannot run UI.");
                return;
            }
            try
            {
                windowController.ShowModal();
            }
            catch (Exception ex)
            {
                log.Error("WindowController ShowModal failed. {0}", ex);
            }
        }

        public void RunUI(UIControllerFlow controllerFlow, [AllowNull] IConnection connection)
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot run UI for {0}.", controllerFlow);
                return;
            }

            SetupUI(controllerFlow, connection);
            try
            {
                windowController.ShowModal();
            }
            catch (Exception ex)
            {
                log.Error("WindowController ShowModal failed for {0}. {1}", controllerFlow, ex);
            }
        }

        public void StopUI()
        {
            if (!Initialized)
            {
                log.Error("ExportProvider is not initialized, cannot stop UI.");
                return;
            }

            StopUI(currentUIFlow);
            currentUIFlow = null;
        }

        static void StopUI(ExportLifetimeContext<IUIController> disposable)
        {
            try {
                if (disposable != null && disposable.Value != null)
                {
                    if (!disposable.Value.IsStopped)
                        disposable.Value.Stop();
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Error("Failed to dispose UI. {0}", ex);
            }
        }

        T AddToDisposables<T>(T instance)
        {
            var disposable = instance as IDisposable;
            if (disposable != null)
            {
                disposables.Add(disposable);
            }
            return instance;
        }

        void StopUIFlowWhenWindowIsClosedByUser(object sender, EventArgs e)
        {
            StopUI();
        }

        bool disposed;
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (disposed) return;

                StopUI();
                if (disposables != null)
                    disposables.Dispose();
                disposables = null;
                if (tempContainer != null)
                    tempContainer.Dispose();
                tempContainer = null;
                disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
