Pod::Spec.new do |s|
  s.name           = 'RtxLensTransport'
  s.version        = '0.1.0'
  s.summary        = 'Pinned transport for the RTX Lens service'
  s.homepage       = 'https://rtxlens.com'
  s.license        = 'LGPL-3.0'
  s.author         = 'RTX Lens'
  s.source         = { :git => 'https://github.com/Codeh4ck/RTXLens.git' }
  s.platforms      = { :ios => '16.4' }
  s.static_framework = true
  s.swift_version = '5.0'

  s.dependency 'ExpoModulesCore'
  s.source_files = '**/*.swift'
end
