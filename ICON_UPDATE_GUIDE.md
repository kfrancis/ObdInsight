# ObdInsight - Icon and Splash Screen Update Guide

## What Was Changed

### 1. Custom Splash Screen
- **File**: `src/ObdInsight/Resources/Splash/splash.svg`
- **Design**: Features the "ObdInsight" app name with an OBD-II connector icon and signal waves
- **Colors**: White text and graphics on the #512BD4 purple background

### 2. Custom App Icon
- **Background**: `src/ObdInsight/Resources/AppIcon/appicon.svg` - Purple gradient background
- **Foreground**: `src/ObdInsight/Resources/AppIcon/appiconfg.svg` - OBD connector with 16 pins, status LEDs, and "OI" monogram

### 3. Tab Bar Icons
- **File**: `src/ObdInsight/AppShell.xaml` - Added Icon properties to each tab
- **Created Icons**:
  - `src/ObdInsight/Resources/Images/home.svg` - Home icon
  - `src/ObdInsight/Resources/Images/devices.svg` - Bluetooth/device icon
  - `src/ObdInsight/Resources/Images/diagnostics.svg` - Analytics/chart icon

## Why Icons/Splash Didn't Update on iPhone

iOS aggressively caches app icons and splash screens. Here's what you need to do:

### For iOS Deployment:

1. **Uninstall the app from your iPhone**
   - Long press the ObdInsight app icon
   - Tap "Remove App" ? "Delete App"

2. **Clean the build in Visual Studio**
   - Build ? Clean Solution
   - Manually delete `bin` and `obj` folders if needed

3. **Clean iOS build cache (if using Mac for building)**
   - In Xcode: Product ? Clean Build Folder (Shift+Cmd+K)
   - Or delete: `~/Library/Developer/Xcode/DerivedData`

4. **Rebuild and redeploy**
   ```powershell
   # Run the provided script
   .\clean-rebuild.ps1
   ```

5. **Restart your iPhone** (optional but sometimes necessary)
   - Power off and back on to clear iOS cache

### Alternative: Change App Identifier (Quick Test)

If you want to test immediately, temporarily change the app identifier in `ObdInsight.csproj`:

```xml
<!-- Change from -->
<ApplicationId>com.companyname.obdinsight</ApplicationId>

<!-- To -->
<ApplicationId>com.companyname.obdinsight.v2</ApplicationId>
```

This will make iOS treat it as a new app, but you'll lose any existing app data.

## Build Status

? **Android**: Build successful with new icons
? **iOS**: Requires Mac for building (Windows-only iOS build has SDK issues)

## Next Steps

1. Run the clean-rebuild script:
   ```powershell
   .\clean-rebuild.ps1
   ```

2. Uninstall the app from your iPhone

3. Deploy the app again through Visual Studio or Xcode

4. The new icon and splash screen should now appear!

## Troubleshooting

### If icons still don't update:
- Check iOS Settings ? General ? iPhone Storage ? Find ObdInsight ? Delete
- Restart iPhone
- Redeploy

### If tabs don't show icons:
- Verify the SVG files exist in `src/ObdInsight/Resources/Images/`
- Check the AppShell.xaml has `Icon="home.png"` etc. (MAUI converts SVG to PNG)
- Clean and rebuild

### Build errors:
- For iOS on Windows: Use a Mac or Mac Build Host
- For Android: Ensure Android SDK is properly installed

## Files Modified

1. `src/ObdInsight/Resources/Splash/splash.svg` - Updated splash screen
2. `src/ObdInsight/Resources/AppIcon/appicon.svg` - Updated icon background
3. `src/ObdInsight/Resources/AppIcon/appiconfg.svg` - Updated icon foreground
4. `src/ObdInsight/AppShell.xaml` - Added tab icons
5. `src/ObdInsight/Resources/Images/home.svg` - Created
6. `src/ObdInsight/Resources/Images/devices.svg` - Created
7. `src/ObdInsight/Resources/Images/diagnostics.svg` - Created
8. `clean-rebuild.ps1` - Created helper script

## Design Notes

The design features:
- **OBD-II Connector**: Representing the app's core functionality
- **16 Pins**: Accurate representation of actual OBD-II connectors
- **Status LEDs**: Green and blue lights suggesting active diagnostics
- **Signal Waves**: Visual representation of data transmission
- **Professional Purple**: Using the #512BD4 brand color
- **Clean Typography**: Bold, readable "ObdInsight" text
