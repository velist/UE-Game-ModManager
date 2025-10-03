using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace UEModManager
{
    public class DragAdorner : Adorner
    {
        public DragAdorner(UIElement adornedElement) : base(adornedElement)
        {
        }
        
        public DragAdorner(UIElement adornedElement, object content, Size size) : base(adornedElement)
        {
        }
        
        public void UpdatePosition(Point position) 
        { 
            // Dummy implementation for drag position updates
        }
        
        protected override void OnRender(DrawingContext drawingContext)
        {
            // Dummy rendering implementation
        }
    }
}