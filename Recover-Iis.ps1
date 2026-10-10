$ErrorActionPreference = "Continue"
Import-Module WebAdministration
Start-WebAppPool -Name "MaterialOutboundApiPool" -ErrorAction Continue
Start-Sleep -Seconds 2
Start-Website -Name "MaterialOutboundApi" -ErrorAction Continue
