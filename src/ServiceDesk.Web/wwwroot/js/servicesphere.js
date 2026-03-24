/* =====================================================================
   ServiceSphere - Custom JavaScript
   Jobick's custom.min.js handles sidebar, dark mode, and MetisMenu.
   This file adds ServiceSphere-specific UI behavior only.
   ===================================================================== */

document.addEventListener('DOMContentLoaded', function () {

    // ----- Bootstrap Tooltips Init -----
    const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
    tooltipTriggerList.forEach(function (el) {
        new bootstrap.Tooltip(el);
    });

});
