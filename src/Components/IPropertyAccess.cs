using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace ProcessRecorderApp.Components;

public interface IPropertyAccess
{
    IEnumerable<PropertyInfo> GetProperties();
}
