using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;

namespace PlumbingCalculatorAddin
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        // Use a static variable to hold a reference to the window.
        private static CalculatorWindow _instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // If the window is null (meaning it's not open), create a new one.
                if (_instance == null)
                {
                    _instance = new CalculatorWindow(commandData.Application);
                    
                    _instance.Show();
                }
                else
                {
                    // If the window already exists, just bring it to the front.
                    _instance.Show();
                    _instance.Activate();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}