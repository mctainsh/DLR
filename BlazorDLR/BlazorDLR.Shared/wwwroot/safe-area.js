/*
	Which edge the camera cutout is on, published to CSS as `data-notch` on <html>.

	WHY THIS EXISTS. In landscape, iOS reports env(safe-area-inset-left) AND
	env(safe-area-inset-right) as the same non-zero number - 44 pt on a notched iPhone,
	more on a Dynamic Island one - whichever side the cutout is physically on. UIKit
	mirrors the inset so that a layout does not jump when the phone is flipped end for
	end. A layout that honours both therefore keeps the cutout's width of clearance on
	the edge that has no cutout, which is the strip of dead rail that showed to the left
	of the nav icons in landscape with the island on the right.

	CSS cannot tell the two apart; the orientation angle can, so this puts the answer
	where CSS can read it and app.css narrows --dlr-safe-left / --dlr-safe-right from it.

	`screen.orientation.angle` and NOT the legacy `window.orientation`: the two use
	opposite signs for landscape on iOS, and picking the wrong one would put the icons
	back under the cutout in exactly one of the two orientations. Per the Screen
	Orientation spec the angle is counter-clockwise from the device's natural
	orientation, so at 90° the natural top edge - the one the camera lives on - has
	swung round to the LEFT, and at 270° to the right.

	No attribute at all when the angle is unreadable. That is deliberate: the CSS
	default is the full symmetric inset, which wastes a strip but never hides an icon,
	and it is also what every non-phone host gets, where these insets are 0 anyway.
*/

(function () {
	"use strict";

	var root = document.documentElement;
	var orientation = window.screen ? window.screen.orientation : null;

	function notchSide() {
		if (!orientation || typeof orientation.angle !== "number") {
			return null;
		}

		var angle = ((orientation.angle % 360) + 360) % 360;

		if (angle === 90) {
			return "left";
		}

		if (angle === 270) {
			return "right";
		}

		// Portrait either way up: the cutout is on an edge no side inset describes, and
		// iOS reports 0 for both of them.
		return "none";
	}

	function apply() {
		var side = notchSide();

		// Written only on a change - this runs on every resize event, and re-setting an
		// attribute to the value it already holds still invalidates style.
		if (side === null) {
			root.removeAttribute("data-notch");
		}
		else if (root.getAttribute("data-notch") !== side) {
			root.setAttribute("data-notch", side);
		}
	}

	apply();

	if (orientation && orientation.addEventListener) {
		orientation.addEventListener("change", apply);
	}

	// The belt to that braces: a WebView that rotates without firing an orientation
	// change still resizes, and the rail has to be right by the time the frame paints.
	window.addEventListener("resize", apply);
})();
