using System.Xml.Linq;

namespace Valenius.Service;

internal static class UpdateTaskDefinition
{
    internal static string Create(string scriptPath)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "System"),
                    new XElement(ns + "UserId", "S-1-5-18"),
                    new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "AllowStartOnDemand", true),
                new XElement(ns + "Enabled", true)),
            new XElement(ns + "Actions", new XAttribute("Context", "System"),
                new XElement(ns + "Exec", new XElement(ns + "Command", Path.GetFullPath(scriptPath)))))).ToString();
    }
}
