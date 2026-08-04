"""IPC message types — mirrors Valenius.Shared.IpcMessages.

JSON field names use PascalCase to match .NET's default serialisation.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional


class CommandType(IntEnum):
    Connect = 0
    Disconnect = 1
    Status = 2
    UploadConfig = 3
    GetConfigInfo = 4
    CheckUpdate = 5
    Register = 6
    SyncStatus = 7
    SetAutoConnect = 8
    DeleteProfile = 9
    NotifyOffline = 10
    MfaEnrollConfirm = 11
    SendLogs = 12
    SetBackendUrl = 13


@dataclass
class PipeCommand:
    Command: CommandType
    ConfigContent: Optional[str] = None
    ProfileName: Optional[str] = None
    AutoConnectEnabled: Optional[bool] = None
    MfaCode: Optional[str] = None
    BackendDns: Optional[str] = None

    def to_json(self) -> str:
        d: dict = {'Command': int(self.Command)}
        if self.ConfigContent is not None:
            d['ConfigContent'] = self.ConfigContent
        if self.ProfileName is not None:
            d['ProfileName'] = self.ProfileName
        if self.AutoConnectEnabled is not None:
            d['AutoConnectEnabled'] = self.AutoConnectEnabled
        if self.MfaCode is not None:
            d['MfaCode'] = self.MfaCode
        if self.BackendDns is not None:
            d['BackendDns'] = self.BackendDns
        return json.dumps(d)

    @staticmethod
    def from_json(text: str) -> PipeCommand:
        d = json.loads(text)
        return PipeCommand(
            Command=CommandType(d['Command']),
            ConfigContent=d.get('ConfigContent'),
            ProfileName=d.get('ProfileName'),
            AutoConnectEnabled=d.get('AutoConnectEnabled'),
            MfaCode=d.get('MfaCode'),
            BackendDns=d.get('BackendDns'),
        )


@dataclass
class PipeResponse:
    Success: bool
    Error: Optional[str] = None
    DataJson: Optional[str] = None
    # True when Error is a pre-connect local-LAN-conflict refusal (this machine's own LAN
    # overlaps a network reachable through the VPN, or the assigned VPN IP itself). Mirrors
    # Windows PipeResponse.IsLanConflict / macOS PipeResponse.isLanConflict.
    IsLanConflict: bool = False

    def to_json(self) -> str:
        d: dict = {'Success': self.Success}
        if self.Error is not None:
            d['Error'] = self.Error
        if self.DataJson is not None:
            d['DataJson'] = self.DataJson
        if self.IsLanConflict:
            d['IsLanConflict'] = self.IsLanConflict
        return json.dumps(d)

    @staticmethod
    def from_json(text: str) -> PipeResponse:
        d = json.loads(text)
        return PipeResponse(
            Success=d['Success'],
            Error=d.get('Error'),
            DataJson=d.get('DataJson'),
            IsLanConflict=d.get('IsLanConflict', False),
        )


@dataclass
class ConnectedTunnelInfo:
    Name: str = ""
    IsVerified: bool = False
    VerificationFailed: bool = False
    ConnectedUser: Optional[str] = None
    ConnectedSince: Optional[str] = None

    def to_dict(self) -> dict:
        return {
            'Name': self.Name,
            'IsVerified': self.IsVerified,
            'VerificationFailed': self.VerificationFailed,
            'ConnectedUser': self.ConnectedUser,
            'ConnectedSince': self.ConnectedSince,
        }

    @staticmethod
    def from_dict(d: dict) -> ConnectedTunnelInfo:
        return ConnectedTunnelInfo(
            Name=d.get('Name', ''),
            IsVerified=d.get('IsVerified', False),
            VerificationFailed=d.get('VerificationFailed', False),
            ConnectedUser=d.get('ConnectedUser'),
            ConnectedSince=d.get('ConnectedSince'),
        )


@dataclass
class TunnelStatus:
    IsConnected: bool = False
    IsVerified: bool = False
    ConnectedUser: Optional[str] = None
    TunnelName: Optional[str] = None
    ConnectedSince: Optional[str] = None
    ConnectedTunnels: list[ConnectedTunnelInfo] = field(default_factory=list)
    RegistrationIsActive: Optional[bool] = None
    HasStagedConfig: bool = False
    UpdateAvailable: bool = False
    AvailableProfiles: list[str] = field(default_factory=list)
    DeletableProfiles: list[str] = field(default_factory=list)
    AutoConnectEnabled: bool = False
    MfaRequired: bool = False
    MfaAuthorizeUrl: Optional[str] = None
    MfaSessionExpiresAt: Optional[str] = None
    MfaEnrollmentOpen: bool = False
    MfaEnrollmentUri: Optional[str] = None
    MfaApproveNumber: Optional[int] = None
    BackendUrl: Optional[str] = None
    BackendReachable: Optional[bool] = None
    DaemonVersion: Optional[str] = None
    BackendUnconfigured: bool = False

    def to_json(self) -> str:
        return json.dumps({
            'IsConnected': self.IsConnected,
            'IsVerified': self.IsVerified,
            'ConnectedUser': self.ConnectedUser,
            'TunnelName': self.TunnelName,
            'ConnectedSince': self.ConnectedSince,
            'ConnectedTunnels': [t.to_dict() for t in self.ConnectedTunnels],
            'RegistrationIsActive': self.RegistrationIsActive,
            'HasStagedConfig': self.HasStagedConfig,
            'UpdateAvailable': self.UpdateAvailable,
            'AvailableProfiles': self.AvailableProfiles,
            'DeletableProfiles': self.DeletableProfiles,
            'AutoConnectEnabled': self.AutoConnectEnabled,
            'MfaRequired': self.MfaRequired,
            'MfaAuthorizeUrl': self.MfaAuthorizeUrl,
            'MfaSessionExpiresAt': self.MfaSessionExpiresAt,
            'MfaEnrollmentOpen': self.MfaEnrollmentOpen,
            'MfaEnrollmentUri': self.MfaEnrollmentUri,
            'MfaApproveNumber': self.MfaApproveNumber,
            'BackendUrl': self.BackendUrl,
            'BackendReachable': self.BackendReachable,
            'DaemonVersion': self.DaemonVersion,
            'BackendUnconfigured': self.BackendUnconfigured,
        })

    @staticmethod
    def from_json(text: str) -> TunnelStatus:
        d = json.loads(text)
        raw_tunnels = d.get('ConnectedTunnels') or []
        return TunnelStatus(
            IsConnected=d.get('IsConnected', False),
            IsVerified=d.get('IsVerified', False),
            ConnectedUser=d.get('ConnectedUser'),
            TunnelName=d.get('TunnelName'),
            ConnectedSince=d.get('ConnectedSince'),
            ConnectedTunnels=[ConnectedTunnelInfo.from_dict(t) for t in raw_tunnels],
            RegistrationIsActive=d.get('RegistrationIsActive'),
            HasStagedConfig=d.get('HasStagedConfig', False),
            UpdateAvailable=d.get('UpdateAvailable', False),
            AvailableProfiles=d.get('AvailableProfiles') or [],
            DeletableProfiles=d.get('DeletableProfiles') or [],
            AutoConnectEnabled=d.get('AutoConnectEnabled', False),
            MfaRequired=d.get('MfaRequired', False),
            MfaAuthorizeUrl=d.get('MfaAuthorizeUrl'),
            MfaSessionExpiresAt=d.get('MfaSessionExpiresAt'),
            MfaEnrollmentOpen=d.get('MfaEnrollmentOpen', False),
            MfaEnrollmentUri=d.get('MfaEnrollmentUri'),
            MfaApproveNumber=d.get('MfaApproveNumber'),
            BackendUrl=d.get('BackendUrl'),
            BackendReachable=d.get('BackendReachable'),
            DaemonVersion=d.get('DaemonVersion'),
            BackendUnconfigured=d.get('BackendUnconfigured', False),
        )


@dataclass
class ConfigInfo:
    HasConfig: bool = False
    TunnelName: Optional[str] = None
    FileName: Optional[str] = None
    Profiles: list[str] = field(default_factory=list)

    def to_json(self) -> str:
        return json.dumps({
            'HasConfig': self.HasConfig,
            'TunnelName': self.TunnelName,
            'FileName': self.FileName,
            'Profiles': self.Profiles,
        })

    @staticmethod
    def from_json(text: str) -> ConfigInfo:
        d = json.loads(text)
        return ConfigInfo(
            HasConfig=d.get('HasConfig', False),
            TunnelName=d.get('TunnelName'),
            FileName=d.get('FileName'),
            Profiles=d.get('Profiles') or [],
        )


@dataclass
class RegistrationResult:
    IsActive: bool = False
    Message: Optional[str] = None

    def to_json(self) -> str:
        return json.dumps({'IsActive': self.IsActive, 'Message': self.Message})

    @staticmethod
    def from_json(text: str) -> RegistrationResult:
        d = json.loads(text)
        return RegistrationResult(IsActive=d.get('IsActive', False), Message=d.get('Message'))


@dataclass
class VersionCheckResult:
    UpdateAvailable: bool = False
    CurrentVersion: Optional[str] = None
    LatestVersion: Optional[str] = None
    ReleaseNotes: Optional[str] = None

    def to_json(self) -> str:
        return json.dumps({
            'UpdateAvailable': self.UpdateAvailable,
            'CurrentVersion': self.CurrentVersion,
            'LatestVersion': self.LatestVersion,
            'ReleaseNotes': self.ReleaseNotes,
        })

    @staticmethod
    def from_json(text: str) -> VersionCheckResult:
        d = json.loads(text)
        return VersionCheckResult(
            UpdateAvailable=d.get('UpdateAvailable', False),
            CurrentVersion=d.get('CurrentVersion'),
            LatestVersion=d.get('LatestVersion'),
            ReleaseNotes=d.get('ReleaseNotes'),
        )
