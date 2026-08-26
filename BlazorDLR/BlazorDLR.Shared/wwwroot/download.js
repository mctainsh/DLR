// Saving a file from a browser page. There is no API for it — the only route is an
// anchor carrying a download attribute and a synthetic click, so that is what this does.
//
// A Blob URL rather than a data: URL: an account export is a zip that runs to megabytes,
// and base64 in an href pushes a multi-megabyte string through the interop boundary and
// then past whatever URL length the browser happens to enforce. A Blob is passed by
// reference and has no length limit.
export function save(fileName, contentType, bytes) {
	const blob = new Blob([bytes], { type: contentType || 'application/octet-stream' });
	const url = URL.createObjectURL(blob);

	const anchor = document.createElement('a');
	anchor.href = url;
	anchor.download = fileName;
	anchor.rel = 'noopener';
	anchor.style.display = 'none';

	// In the DOM before the click: Firefox ignores a click on a detached anchor.
	document.body.appendChild(anchor);
	anchor.click();
	anchor.remove();

	// Revoking immediately races the download the click just started, so leave the URL
	// alive long enough for the browser to have read it and then let the blob go.
	setTimeout(() => URL.revokeObjectURL(url), 60000);
}
