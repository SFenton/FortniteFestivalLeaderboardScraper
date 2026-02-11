package com.fortnitefestivalrn

import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.os.Bundle
import androidx.core.content.ContextCompat
import androidx.window.java.layout.WindowInfoTrackerCallbackAdapter
import androidx.window.layout.FoldingFeature
import androidx.window.layout.WindowInfoTracker
import androidx.window.layout.WindowLayoutInfo
import com.facebook.react.ReactActivity
import com.facebook.react.ReactActivityDelegate
import com.facebook.react.defaults.DefaultNewArchitectureEntryPoint.fabricEnabled
import com.facebook.react.defaults.DefaultReactActivityDelegate
import androidx.core.util.Consumer

/**
 * Orientation locking rules (Android only — iOS is handled via Info.plist):
 *
 * • Phone              → locked to portrait
 * • Tablet             → free rotation
 * • Foldable (closed)  → locked to portrait
 * • Foldable (open)    → locked to landscape
 *
 * Detection uses Jetpack WindowManager's [WindowInfoTracker]. When the inner
 * display is active the [WindowLayoutInfo] will contain a [FoldingFeature];
 * its absence means the outer (phone-size) screen is in use.
 */
class MainActivity : ReactActivity() {

  private var windowTracker: WindowInfoTrackerCallbackAdapter? = null
  private var layoutConsumer: Consumer<WindowLayoutInfo>? = null

  /**
   * Once we observe a [FoldingFeature] at least once we know the device is a
   * foldable. This lets us distinguish "closed foldable" (small screen, no
   * folding feature) from "regular phone" (also small screen, never has a
   * folding feature) — without requiring a hinge-angle sensor check.
   */
  private var everSeenFoldingFeature = false

  override fun getMainComponentName(): String = "FortniteFestivalRN"

  override fun createReactActivityDelegate(): ReactActivityDelegate =
      DefaultReactActivityDelegate(this, mainComponentName, fabricEnabled)

  // ── Lifecycle ────────────────────────────────────────────────────

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)

    // Lock phones to portrait right away (before React loads).
    // Tablets keep the default (free rotation). Foldable tracking in
    // onStart() will refine the lock once WindowInfoTracker emits.
    val screenSize =
      resources.configuration.screenLayout and Configuration.SCREENLAYOUT_SIZE_MASK
    if (screenSize < Configuration.SCREENLAYOUT_SIZE_LARGE) {
      requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
    }
  }

  override fun onStart() {
    super.onStart()
    startFoldableTracking()
  }

  override fun onStop() {
    stopFoldableTracking()
    super.onStop()
  }

  // ── Foldable tracking ────────────────────────────────────────────

  private fun startFoldableTracking() {
    if (windowTracker != null) return

    val adapter = WindowInfoTrackerCallbackAdapter(
      WindowInfoTracker.getOrCreate(this),
    )
    val consumer = Consumer<WindowLayoutInfo> { info -> applyOrientation(info) }

    adapter.addWindowLayoutInfoListener(
      this,
      ContextCompat.getMainExecutor(this),
      consumer,
    )
    windowTracker = adapter
    layoutConsumer = consumer
  }

  private fun stopFoldableTracking() {
    layoutConsumer?.let { windowTracker?.removeWindowLayoutInfoListener(it) }
    windowTracker = null
    layoutConsumer = null
  }

  /**
   * Called whenever the window layout changes (including fold / unfold events).
   *
   * • [FoldingFeature] present  → foldable is **open**  → sensor landscape
   * • No feature, large screen  → regular **tablet**     → free rotation
   * • No feature, small screen  → **phone** or closed foldable → portrait
   */
  private fun applyOrientation(layoutInfo: WindowLayoutInfo) {
    val foldingFeature = layoutInfo.displayFeatures
      .filterIsInstance<FoldingFeature>()
      .firstOrNull()

    if (foldingFeature != null) {
      // Inner display active, hinge visible → foldable is open.
      everSeenFoldingFeature = true
      requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
    } else {
      val screenSize =
        resources.configuration.screenLayout and Configuration.SCREENLAYOUT_SIZE_MASK
      val isLargeScreen = screenSize >= Configuration.SCREENLAYOUT_SIZE_LARGE

      if (isLargeScreen && !everSeenFoldingFeature) {
        // Non-foldable large screen → tablet → allow all orientations.
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
      } else {
        // Regular phone **or** foldable that is now closed (outer screen).
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
      }
    }
  }
}
