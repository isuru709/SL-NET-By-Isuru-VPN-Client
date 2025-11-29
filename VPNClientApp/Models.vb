Imports System.ComponentModel

' Base Configuration Interface
Public Interface IVPNConfig
    Property Host As String
    Property Port As Integer
    Property Tag As String
    Function GetProtocolType() As String
End Interface

' VLESS Configuration Model
Public Class VLessConfig
    Implements INotifyPropertyChanged, IVPNConfig

    Private _uuid As String
    Private _host As String
    Private _port As Integer
    Private _security As String
    Private _sni As String
    Private _transportType As String
    Private _path As String
    Private _tag As String
    Private _rawLink As String

    Public Property UUID As String
        Get
            Return _uuid
        End Get
        Set(value As String)
            _uuid = value
            OnPropertyChanged(NameOf(UUID))
        End Set
    End Property

    Public Property Host As String Implements IVPNConfig.Host
        Get
            Return _host
        End Get
        Set(value As String)
            _host = value
            OnPropertyChanged(NameOf(Host))
        End Set
    End Property

    Public Property Port As Integer Implements IVPNConfig.Port
        Get
            Return _port
        End Get
        Set(value As Integer)
            _port = value
            OnPropertyChanged(NameOf(Port))
        End Set
    End Property

    Public Property Security As String
        Get
            Return _security
        End Get
        Set(value As String)
            _security = value
            OnPropertyChanged(NameOf(Security))
        End Set
    End Property

    Public Property SNI As String
        Get
            Return _sni
        End Get
        Set(value As String)
            _sni = value
            OnPropertyChanged(NameOf(SNI))
        End Set
    End Property

    Public Property TransportType As String
        Get
            Return _transportType
        End Get
        Set(value As String)
            _transportType = value
            OnPropertyChanged(NameOf(TransportType))
        End Set
    End Property

    Public Property Path As String
        Get
            Return _path
        End Get
        Set(value As String)
            _path = value
            OnPropertyChanged(NameOf(Path))
        End Set
    End Property

    Public Property Tag As String Implements IVPNConfig.Tag
        Get
            Return _tag
        End Get
        Set(value As String)
            _tag = value
            OnPropertyChanged(NameOf(Tag))
        End Set
    End Property

    Public Property RawLink As String
        Get
            Return _rawLink
        End Get
        Set(value As String)
            _rawLink = value
            OnPropertyChanged(NameOf(RawLink))
        End Set
    End Property

    Public Property Fingerprint As String
    Public Property AllowInsecure As Boolean
    Public Property ALPN As String
    Public Property Encryption As String

    Public Function GetProtocolType() As String Implements IVPNConfig.GetProtocolType
        Return "VLESS"
    End Function

    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

    Protected Sub OnPropertyChanged(propertyName As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(propertyName))
    End Sub
End Class

' VMESS Configuration Model
Public Class VMESSConfig
    Implements IVPNConfig

    Public Property Host As String Implements IVPNConfig.Host
    Public Property Port As Integer Implements IVPNConfig.Port
    Public Property UUID As String
    Public Property AlterID As Integer = 0
    Public Property Security As String = "auto"
    Public Property Network As String = "tcp"
    Public Property Path As String
    Public Property TLS As String
    Public Property Tag As String Implements IVPNConfig.Tag

    Public Function GetProtocolType() As String Implements IVPNConfig.GetProtocolType
        Return "VMESS"
    End Function
End Class

' SSH+TLS Configuration Model - Enhanced with key auth and transport options
Public Class SSHTLSConfig
    Implements IVPNConfig

    Public Property Host As String Implements IVPNConfig.Host
    Public Property Port As Integer Implements IVPNConfig.Port
    Public Property Username As String
    Public Property Password As String
    Public Property PrivateKeyPath As String ' Path to SSH private key file (optional)
    Public Property Passphrase As String ' Passphrase for encrypted private key (optional)
    Public Property UseKeyAuth As Boolean = False ' True to use key auth, False for password auth
    Public Property SNI As String
    Public Property Tag As String Implements IVPNConfig.Tag
    Public Property LocalPort As Integer
    Public Property IsOnlineConfig As Boolean = False ' True if from website, False if user-saved
    Public Property UseTLS As Boolean = True ' Enable TLS wrapping for SSH connection
    
    ' Advanced transport options
    Public Property TransportType As String = "direct" ' direct, ws, grpc, h2
    Public Property TransportPath As String ' Path for WebSocket/gRPC/H2 transport
    Public Property TransportHost As String ' Host header for WebSocket/H2 transport
    Public Property EnableCompression As Boolean = False ' Enable SSH compression
    Public Property ProxyJumpHost As String ' SSH proxy jump host (optional)
    
    ' 3x-ui panel specific options
    Public Property PanelType As String = "custom" ' custom, 3x-ui, x-ui, v2ray-ui
    Public Property PanelUrl As String ' URL to 3x-ui panel (optional)
    Public Property InboundTag As String ' Inbound tag from panel (optional)

    Public Function GetProtocolType() As String Implements IVPNConfig.GetProtocolType
        Return "SSH+TLS"
    End Function
End Class

' Legacy SSH Configuration Model
Public Class SSHConfig
    Public Property Host As String
    Public Property Port As Integer = 22
    Public Property Username As String
    Public Property Password As String
    Public Property PrivateKeyPath As String
End Class

' TLS Configuration Model
Public Class TLSConfig
    Public Property Host As String
    Public Property Port As Integer = 443
    Public Property CertificatePath As String
    Public Property VerifySSL As Boolean = True
End Class

' Application Configuration
Public Class AppConfig
    Public Property CurrentConfig As Object ' Can be VLessConfig, VMESSConfig, or SSHTLSConfig
    Public Property SSHConfig As SSHConfig
    Public Property TLSConfig As TLSConfig
    Public Property LastUpdateTime As DateTime
    Public Property ConfigExpireTime As DateTime
    Public Property ConfigVersion As String = "1.0"
End Class

' Blocker settings for domain-based filtering at the proxy
Public Class BlockerSettings
    Public Property AdsEnabled As Boolean
    Public Property AdultEnabled As Boolean
    Public Property SocialEnabled As Boolean
    Public Property CustomDomains As List(Of String)
    Public Property WhitelistDomains As List(Of String)
    ' Advanced list sources
    Public Property UseOisdSmall As Boolean = True
    Public Property UseOisdMedium As Boolean = False
    Public Property UseOisdFull As Boolean = False
    Public Property BlocklistLastUpdated As String

    Public Sub New()
        CustomDomains = New List(Of String)()
        WhitelistDomains = New List(Of String)()
    End Sub
End Class

' Split Tunnel settings for app-based and SNI-based routing
Public Class SplitTunnelSettings
    Public Property AppBasedEnabled As Boolean
    Public Property AppPaths As List(Of String)
    Public Property CustomSNIEnabled As Boolean
    Public Property CustomSNIs As List(Of String)

    Public Sub New()
        AppBasedEnabled = False
        AppPaths = New List(Of String)()
        CustomSNIEnabled = False
        CustomSNIs = New List(Of String)()
    End Sub
End Class
