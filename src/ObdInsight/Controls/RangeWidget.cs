using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObdInsight.Controls
{
    /// <summary>
    /// Smart widget that displays vehicle range by subscribing to range data messages.
    /// </summary>
    public partial class RangeWidget : ContentView
    {
        private readonly NumberWidget _numberWidget;
        private readonly IMessenger _messenger;

        public RangeWidget()
        {
            _messenger = WeakReferenceMessenger.Default;

            _numberWidget = new NumberWidget
            {
                Title = "Range",
                Unit = "km",
                Icon = "\uf5d0", // FontAwesome road icon
                ShowIcon = true,
                Format = "F1"
            };

            Content = _numberWidget;
        }

        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();

            if (Handler != null)
            {
                // Subscribe when widget is attached to visual tree
                _messenger.Register<RangeDataMessage>(this, OnRangeDataReceived);
            }
            else
            {
                // Unsubscribe when removed
                _messenger.Unregister<RangeDataMessage>(this);
            }
        }

        private void OnRangeDataReceived(object recipient, RangeDataMessage message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _numberWidget.Value = message.RangeKm;
            });
        }
    }

    // Message definition
    public record RangeDataMessage(double RangeKm);
}
