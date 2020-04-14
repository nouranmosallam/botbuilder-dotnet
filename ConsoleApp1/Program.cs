using System;
using System.Collections.Generic;
using Microsoft.Bot.Builder.LanguageGeneration;

namespace ConsoleApp1
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var templates = Templates.ParseText("# basicTemplate\r\n- Hi\r\n- Hello");

            templates.AddTemplate("newtemplate", new List<string> { "age", "name" }, "- hi ");

            templates.AddTemplate("newtemplate2", null, "- hi2 ");

            templates.UpdateTemplate("newtemplate", "newtemplateName", new List<string> { "newage", "newname" }, "- new hi\r\n#hi");

            templates.UpdateTemplate("newtemplate2", "newtemplateName2", new List<string> { "newage2", "newname2" }, "- new hi\r\n#hi2\r\n");

            templates.DeleteTemplate("newtemplateName");

            templates.DeleteTemplate("newtemplateName2");
        }
    }
}
