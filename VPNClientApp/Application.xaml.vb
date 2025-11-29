Namespace VPNClientApp
    Class Application
        Inherits System.Windows.Application

        ' Application-level events can be handled here if needed
        Private Sub Application_DispatcherUnhandledException(sender As Object, e As Threading.DispatcherUnhandledExceptionEventArgs) Handles Me.DispatcherUnhandledException
            ' Log unhandled exceptions
            MessageBox.Show($"An unexpected error occurred: {e.Exception.Message}{Environment.NewLine}{Environment.NewLine}Stack Trace:{Environment.NewLine}{e.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            e.Handled = True
        End Sub
    End Class
End Namespace
