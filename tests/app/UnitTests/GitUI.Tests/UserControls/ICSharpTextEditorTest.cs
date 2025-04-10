﻿using System.Reflection;
using System.Xml.Linq;

namespace GitUITests.UserControls
{
    public sealed class ICSharpTextEditorTest
    {
        [Test]
        public void ResourceStreamNamesMatchSyntaxNodes()
        {
            Assembly assembly = typeof(ICSharpCode.TextEditor.Document.ResourceSyntaxModeProvider).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            string syntaxNodesResource = resources.First(r => r.Contains("SyntaxModes"));
            IEnumerable<XElement> xmlModes = XElement.Load(assembly.GetManifestResourceStream(syntaxNodesResource)).Elements("Mode");
            IEnumerable<string> resourcesToCheck = resources.Where(r => r.EndsWith("xshd"));

            var matched = from xml in xmlModes
                          join res in resourcesToCheck on xml.Attribute("file").Value equals
                          res.Replace("ICSharpCode.TextEditor.Resources.", "")
                          select new { ResourceName = res, XMLModeFile = xml.Attribute("file").Value };

            IEnumerable<string> missingInXML = resourcesToCheck.Except(matched.Select(m => m.ResourceName));

            IEnumerable<string> missingInResources = from nd in xmlModes
                                     where !matched.Select(m => m.XMLModeFile).Contains(nd.Attribute("file").Value)
                                     select nd.Attribute("file").Value;

            ClassicAssert.That(!missingInXML.Any(), "The SyntaxNodes.xml file is out of sync with the actual resources. Check the following resource names that don't exist in xml file. {0}", string.Join(",", missingInXML));
            ClassicAssert.That(!missingInResources.Any(), "The SyntaxNodes.xml file is out of sync with the actual resources. Check the following resource names that don't exist in embedded resources. {0}", string.Join(",", missingInResources));
        }
    }
}
