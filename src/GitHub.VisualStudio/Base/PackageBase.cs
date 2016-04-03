﻿using System;
using Microsoft.VisualStudio.Shell;

namespace GitHub.VisualStudio.Base
{
    public abstract class PackageBase : Package
    {
        IServiceProvider serviceProvider;
        protected IServiceProvider ServiceProvider
        {
            get { return serviceProvider; }
            set { serviceProvider = value; }
        }

        protected PackageBase()
        {
            ServiceProvider = this;
        }

        protected PackageBase(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
        }
    }
}
