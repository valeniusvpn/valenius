#
# Valenius WireGuard tunnel plugin — iOS podspec.
#
# This pod is the APP-PROCESS side only (the Flutter plugin that manages the
# tunnel via NETunnelProviderManager). The actual WireGuard tunnel lives in a
# separate Network Extension target — see ios/extension/PacketTunnelProvider.swift
# and README-finish-on-mac.md. `source_files` is deliberately scoped to Classes/**
# so the staged extension/hostapp templates are NOT compiled into this pod.
#
Pod::Spec.new do |s|
  s.name             = 'valenius_vpn'
  s.version          = '0.0.1'
  s.summary          = 'Valenius WireGuard tunnel plugin (iOS).'
  s.description      = 'Manages an NEPacketTunnelProvider-based WireGuard tunnel from the Flutter app process.'
  s.homepage         = 'https://valenius.stranto.com'
  s.license          = { :file => '../LICENSE' }
  s.author           = { 'Stranto' => 'support@stranto.com' }
  s.source           = { :path => '.' }
  s.source_files     = 'Classes/**/*'
  s.dependency 'Flutter'
  s.platform = :ios, '15.0'   # WireGuardKit (in the extension) requires iOS 15+
  s.pod_target_xcconfig = { 'DEFINES_MODULE' => 'YES', 'SWIFT_VERSION' => '5.0' }
  s.swift_version = '5.0'
end
