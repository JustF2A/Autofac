﻿// This software is part of the Autofac IoC container
// Copyright © 2011 Autofac Contributors
// http://autofac.org
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Features.Collections;
using Autofac.Features.Decorators;
using Autofac.Features.GeneratedFactories;
using Autofac.Features.Indexed;
using Autofac.Features.LazyDependencies;
using Autofac.Features.Metadata;
using Autofac.Features.OwnedInstances;

namespace Autofac
{
    /// <summary>
    /// Used to build an <see cref="IContainer"/> from component registrations.
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = new ContainerBuilder();
    ///
    /// builder.RegisterType&lt;Logger&gt;()
    ///     .As&lt;ILogger&gt;()
    ///     .SingleInstance();
    ///
    /// builder.Register(c => new MessageHandler(c.Resolve&lt;ILogger&gt;()));
    ///
    /// var container = builder.Build();
    /// // resolve components from container...
    /// </code>
    /// </example>
    /// <remarks>Most <see cref="ContainerBuilder"/> functionality is accessed
    /// via extension methods in <see cref="RegistrationExtensions"/>.</remarks>
    /// <seealso cref="IContainer"/>
    /// <see cref="RegistrationExtensions"/>
    public class ContainerBuilder
    {
        private readonly IList<DeferredCallback> _configurationCallbacks = new List<DeferredCallback>();
        private bool _wasBuilt;

        private const string BuildCallbackPropertyKey = "__BuildCallbackKey";
        private const string WiredDecoratorsPropertyKey = "__WiredDecorators";

        /// <summary>
        /// Initializes a new instance of the <see cref="ContainerBuilder"/> class.
        /// </summary>
        public ContainerBuilder()
            : this(new Dictionary<string, object>())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ContainerBuilder"/> class.
        /// </summary>
        /// <param name="properties">The properties used during component registration.</param>
        internal ContainerBuilder(IDictionary<string, object> properties)
        {
            Properties = properties;

            if (!Properties.ContainsKey(BuildCallbackPropertyKey))
            {
                Properties.Add(BuildCallbackPropertyKey, new List<Action<IContainer>>());
            }

            if (!Properties.ContainsKey(WiredDecoratorsPropertyKey))
            {
                Properties.Add(WiredDecoratorsPropertyKey, new HashSet<Type>());
            }
        }

        /// <summary>
        /// Gets the set of properties used during component registration.
        /// </summary>
        /// <value>
        /// An <see cref="IDictionary{TKey, TValue}"/> that can be used to share
        /// context across registrations.
        /// </value>
        public IDictionary<string, object> Properties { get; }

        /// <summary>
        /// Register a callback that will be invoked when the container is configured.
        /// </summary>
        /// <remarks>This is primarily for extending the builder syntax.</remarks>
        /// <param name="configurationCallback">Callback to execute.</param>
        public virtual DeferredCallback RegisterCallback(Action<IComponentRegistry> configurationCallback)
        {
            if (configurationCallback == null) throw new ArgumentNullException(nameof(configurationCallback));

            var c = new DeferredCallback(configurationCallback);
            _configurationCallbacks.Add(c);
            return c;
        }

        /// <summary>
        /// Register a callback that will be invoked when the container is built.
        /// </summary>
        /// <param name="buildCallback">Callback to execute.</param>
        /// <returns>The <see cref="ContainerBuilder"/> instance to continue registration calls.</returns>
        public ContainerBuilder RegisterBuildCallback(Action<IContainer> buildCallback)
        {
            if (buildCallback == null) throw new ArgumentNullException(nameof(buildCallback));

            var buildCallbacks = GetBuildCallbacks();
            buildCallbacks.Add(buildCallback);

            return this;
        }

        /// <summary>
        /// Create a new container with the component registrations that have been made.
        /// </summary>
        /// <param name="options">Options that influence the way the container is initialised.</param>
        /// <remarks>
        /// Build can only be called once per <see cref="ContainerBuilder"/>
        /// - this prevents ownership issues for provided instances.
        /// Build enables support for the relationship types that come with Autofac (e.g.
        /// Func, Owned, Meta, Lazy, IEnumerable.) To exclude support for these types,
        /// first create the container, then call Update() on the builder.
        /// </remarks>
        /// <returns>A new container with the configured component registrations.</returns>
        public IContainer Build(ContainerBuildOptions options = ContainerBuildOptions.None)
        {
            var result = new Container(Properties);
            Build(result.ComponentRegistry, (options & ContainerBuildOptions.ExcludeDefaultModules) != ContainerBuildOptions.None);

            if ((options & ContainerBuildOptions.IgnoreStartableComponents) == ContainerBuildOptions.None)
                StartStartableComponents(result);

            var buildCallbacks = GetBuildCallbacks();
            foreach (var buildCallback in buildCallbacks)
                buildCallback(result);

            return result;
        }

        private static void StartStartableComponents(IComponentContext componentContext)
        {
            // We track which registrations have already been auto-activated by adding
            // a metadata value. If the value is present, we won't re-activate. This helps
            // in the container update situation.
            const string started = MetadataKeys.AutoActivated;
            object meta;

            foreach (var startable in componentContext.ComponentRegistry.RegistrationsFor(new TypedService(typeof(IStartable))).Where(r => !r.Metadata.TryGetValue(started, out meta)))
            {
                try
                {
                    var instance = (IStartable)componentContext.ResolveComponent(startable, Enumerable.Empty<Parameter>());
                    instance.Start();
                }
                finally
                {
                    startable.Metadata[started] = true;
                }
            }

            foreach (var registration in componentContext.ComponentRegistry.RegistrationsFor(new AutoActivateService()).Where(r => !r.Metadata.TryGetValue(started, out meta)))
            {
                try
                {
                    componentContext.ResolveComponent(registration, Enumerable.Empty<Parameter>());
                }
                catch (DependencyResolutionException ex)
                {
                    throw new DependencyResolutionException(String.Format(CultureInfo.CurrentCulture, ContainerBuilderResources.ErrorAutoActivating, registration), ex);
                }
                finally
                {
                    registration.Metadata[started] = true;
                }
            }
        }

        /// <summary>
        /// Configure an existing container with the component registrations
        /// that have been made.
        /// </summary>
        /// <remarks>
        /// Update can only be called once per <see cref="ContainerBuilder"/>
        /// - this prevents ownership issues for provided instances.
        /// </remarks>
        /// <param name="container">An existing container to make the registrations in.</param>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "You can't update any arbitrary context, only containers.")]
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IContainer container)
        {
            Update(container, ContainerBuildOptions.None);
        }

        /// <summary>
        /// Configure an existing container with the component registrations
        /// that have been made and allows additional build options to be specified.
        /// </summary>
        /// <remarks>
        /// Update can only be called once per <see cref="ContainerBuilder"/>
        /// - this prevents ownership issues for provided instances.
        /// </remarks>
        /// <param name="container">An existing container to make the registrations in.</param>
        /// <param name="options">Options that influence the way the container is updated.</param>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "You can't update any arbitrary context, only containers.")]
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IContainer container, ContainerBuildOptions options)
        {
            // Issue #462: The ContainerBuildOptions parameter is added here as an overload
            // rather than an optional parameter to avoid method binding issues. In version
            // 4.0 or later we should refactor this to be an optional parameter.
            if (container == null) throw new ArgumentNullException(nameof(container));
            Update(container.ComponentRegistry);
            if ((options & ContainerBuildOptions.IgnoreStartableComponents) == ContainerBuildOptions.None)
                StartStartableComponents(container);
        }

        /// <summary>
        /// Configure an existing registry with the component registrations
        /// that have been made.
        /// </summary>
        /// <remarks>
        /// Update can only be called once per <see cref="ContainerBuilder"/>
        /// - this prevents ownership issues for provided instances.
        /// </remarks>
        /// <param name="componentRegistry">An existing registry to make the registrations in.</param>
        [Obsolete("Containers should generally be considered immutable. Register all of your dependencies before building/resolving. If you need to change the contents of a container, you technically should rebuild the container. This method may be removed in a future major release.")]
        public void Update(IComponentRegistry componentRegistry)
        {
            this.UpdateRegistry(componentRegistry);
        }

        /// <summary>
        /// Configure an existing registry with the component registrations
        /// that have been made. Primarily useful in dynamically adding registrations
        /// to a child lifetime scope.
        /// </summary>
        /// <remarks>
        /// Update can only be called once per <see cref="ContainerBuilder"/>
        /// - this prevents ownership issues for provided instances.
        /// </remarks>
        /// <param name="componentRegistry">An existing registry to make the registrations in.</param>
        internal void UpdateRegistry(IComponentRegistry componentRegistry)
        {
            if (componentRegistry == null) throw new ArgumentNullException(nameof(componentRegistry));
            Build(componentRegistry, true);
        }

        private void Build(IComponentRegistry componentRegistry, bool excludeDefaultModules)
        {
            if (componentRegistry == null) throw new ArgumentNullException(nameof(componentRegistry));

            if (_wasBuilt)
                throw new InvalidOperationException(ContainerBuilderResources.BuildCanOnlyBeCalledOnce);

            _wasBuilt = true;

            if (!excludeDefaultModules)
                RegisterDefaultAdapters(componentRegistry);

            foreach (var callback in _configurationCallbacks)
                callback.Callback(componentRegistry);

            // Build a distinct list of decorated service types.
            var decoratorServiceTypes = componentRegistry.Registrations
                .SelectMany(r => r.Services)
                .OfType<DecoratorService>()
                .Select(ds => ds.ServiceType)
                .Distinct()
                .ToArray();

            // No decorators configured so bail early.
            if (decoratorServiceTypes.Length == 0) return;

            // Enumerate the decorated service types.
            foreach (var decoratorServiceType in decoratorServiceTypes)
            {
                // The typed service being decorated.
                var typedService = new TypedService(decoratorServiceType);

                // TODO: Exception condition?
                if (!componentRegistry.TryGetRegistration(typedService, out var decorated)) continue;

                // Get a list of decorators that have already been wired.
                var wiredDecorators = GetWiredDecorators();

                // Bail if we have already wired up the Activating method.
                if (wiredDecorators.Contains(decoratorServiceType)) continue;

                void ApplyDecorator(object sender, ActivatingEventArgs<object> args)
                {
                    var decoratedInstance = typedService.Decorate(args.Instance, args.Context, args.Parameters);
                    args.ReplaceInstance(decoratedInstance);
                }

                // Wire up the Activating method.
                decorated.Activating += ApplyDecorator;

                // Note that the service type has already been wired.
                wiredDecorators.Add(decoratorServiceType);
            }
        }

        private void RegisterDefaultAdapters(IComponentRegistry componentRegistry)
        {
            this.RegisterGeneric(typeof(KeyedServiceIndex<,>)).As(typeof(IIndex<,>)).InstancePerLifetimeScope();
            componentRegistry.AddRegistrationSource(new CollectionRegistrationSource());
            componentRegistry.AddRegistrationSource(new OwnedInstanceRegistrationSource());
            componentRegistry.AddRegistrationSource(new MetaRegistrationSource());
            componentRegistry.AddRegistrationSource(new LazyRegistrationSource());
            componentRegistry.AddRegistrationSource(new LazyWithMetadataRegistrationSource());
            componentRegistry.AddRegistrationSource(new StronglyTypedMetaRegistrationSource());
            componentRegistry.AddRegistrationSource(new GeneratedFactoryRegistrationSource());
        }

        private List<Action<IContainer>> GetBuildCallbacks()
        {
            return (List<Action<IContainer>>)Properties[BuildCallbackPropertyKey];
        }

        private HashSet<Type> GetWiredDecorators()
        {
            return (HashSet<Type>)Properties[WiredDecoratorsPropertyKey];
        }
    }
}