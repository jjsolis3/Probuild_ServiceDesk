/**
 * ServiceSphere DataTables - Global Initialization
 * Automatically initializes DataTables on tables with class "ss-datatable"
 * Provides: Pagination, Search, Column Sorting, PDF/Excel Export
 *
 * window.load timing note:
 *   Jobick's custom.min.js fires handleSelectPicker() on $(window).on('load'),
 *   which wraps .dataTables_wrapper select with bootstrap-select.
 *   Since our script loads after Jobick, our window.load handler fires after
 *   Jobick's — letting us destroy that wrapper and restore a clean native select.
 *   We also use window.load to initialize liveSearch on form selects.
 */

$(window).on('load', function () {

    // --- Fix DataTables length select ---
    // Jobick's handleSelectPicker wraps .dataTables_wrapper select with
    // bootstrap-select. Destroy it and restore a proper native select.
    $('.dataTables_wrapper .dataTables_length select').each(function () {
        var $sel = $(this);
        if ($.fn.selectpicker && $sel.data('selectpicker')) {
            $sel.selectpicker('destroy');
        }
        // Remove any leftover display:none the selectpicker may have set
        $sel.show()
            .css({
                display: 'inline-block',
                visibility: 'visible',
                opacity: '1',
                color: '#374151',
                width: 'auto'
            });
    });

    // --- Searchable form selects (live-search) + themed filter selects ---
    // Auto-apply bootstrap-select:
    // 1) for filter-bar selects that explicitly opt in via .selectpicker
    // 2) for non-filter form-select controls with more than 7 options
    $('select.form-select').each(function () {
        var $sel = $(this);
        var isFilterBarSelect = $sel.closest('.filter-bar').length > 0;
        var wantsPicker = $sel.hasClass('selectpicker');

        // Skip filter-bar selects unless explicitly opted in
        if (isFilterBarSelect && !wantsPicker) return;
        // Skip DataTables wrappers (handled above)
        if ($sel.closest('.dataTables_wrapper').length > 0) return;
        // Skip if already initialized
        if ($sel.data('selectpicker')) return;
        // Skip selects that explicitly opt out (e.g. settings page fields)
        if ($sel.data('no-picker')) return;

        var pickerOptions;
        if (isFilterBarSelect) {
            pickerOptions = {
                liveSearch: false,
                width: $sel.data('width') || 'fit',
                style: $sel.data('style') || 'btn-outline-secondary btn-sm',
                styleBase: 'btn'
            };
        } else {
            // Only enhance non-filter selects with enough options to benefit from search
            if ($sel.find('option').length <= 7) return;
            pickerOptions = {
                liveSearch: true,
                liveSearchPlaceholder: 'Type to search...',
                size: 8,
                width: '100%',
                style: '',          // remove default btn-light class
                styleBase: 'btn'    // just .btn, we style via .ss-form-select
            };
        }

        $sel.selectpicker(pickerOptions);

        // Tag the wrapper so our CSS (.ss-form-select) applies
        $sel.closest('.bootstrap-select').addClass('ss-form-select');
    });
});

$(document).ready(function () {

    // ---- Full DataTable (pagination, search, sort, export) ----
    $('.ss-datatable').each(function () {
        var $table = $(this);

        // Determine which columns should NOT be sortable (e.g., "Actions" column)
        var columnDefs = [];
        $table.find('thead th').each(function (index) {
            var text = $(this).text().trim().toLowerCase();
            if (text === 'actions' || text === 'action' || $(this).hasClass('no-sort')) {
                columnDefs.push({ orderable: false, targets: index });
            }
        });

        // Check for data attributes for custom config
        var pageLength = parseInt($table.data('page-length')) || 15;
        var exportTitle = $table.data('export-title') || 'ServiceSphere Export';

        $table.DataTable({
            // Pagination
            paging: true,
            pageLength: pageLength,
            lengthMenu: [[10, 15, 25, 50, 100, -1], [10, 15, 25, 50, 100, 'All']],

            // Search
            searching: true,

            // Sorting
            ordering: true,
            columnDefs: columnDefs,

            // Export Buttons
            dom: '<"row align-items-center mb-3"' +
                     '<"col-sm-6 col-md-3"l>' +
                     '<"col-sm-6 col-md-5 text-center"B>' +
                     '<"col-sm-12 col-md-4"f>' +
                 '>' +
                 'rt' +
                 '<"row align-items-center mt-2"' +
                     '<"col-sm-12 col-md-5"i>' +
                     '<"col-sm-12 col-md-7"p>' +
                 '>',
            buttons: [
                {
                    extend: 'excelHtml5',
                    text: '<i class="bi bi-file-earmark-spreadsheet me-1"></i>Excel',
                    className: 'btn btn-sm btn-outline-success',
                    title: exportTitle,
                    exportOptions: { columns: ':not(.no-export)' }
                },
                {
                    extend: 'pdfHtml5',
                    text: '<i class="bi bi-file-earmark-pdf me-1"></i>PDF',
                    className: 'btn btn-sm btn-outline-danger',
                    title: exportTitle,
                    orientation: 'landscape',
                    pageSize: 'LETTER',
                    exportOptions: { columns: ':not(.no-export)' },
                    customize: function (doc) {
                        var branding   = window.ssBranding || {};
                        var company    = branding.companyName || 'ServiceSphere';
                        var brand      = branding.brandColor  || '#4f46e5';
                        var now        = new Date();
                        var dateStr    = now.toLocaleDateString('en-US', { year: 'numeric', month: 'long', day: 'numeric' });

                        // DataTables adds the title as content[0] — capture & remove it
                        var reportTitle = exportTitle;
                        if (doc.content.length > 0 && doc.content[0].style === 'title') {
                            reportTitle = doc.content[0].text || exportTitle;
                            doc.content.splice(0, 1);
                        }

                        // Branded header: company name (left) + report title & date (right)
                        doc.content.unshift(
                            {
                                canvas: [{ type: 'line', x1: 0, y1: 0, x2: 736, y2: 0, lineWidth: 1.5, lineColor: brand }],
                                margin: [0, 0, 0, 10]
                            },
                            {
                                columns: [
                                    {
                                        stack: [
                                            { text: company, bold: true, fontSize: 16, color: brand },
                                            { text: 'IT Service Desk', fontSize: 9, color: '#6b7280', margin: [0, 2, 0, 0] }
                                        ]
                                    },
                                    {
                                        stack: [
                                            { text: reportTitle, bold: true, fontSize: 13, color: '#111827', alignment: 'right' },
                                            { text: 'Generated: ' + dateStr, fontSize: 9, color: '#6b7280', alignment: 'right', margin: [0, 3, 0, 0] }
                                        ]
                                    }
                                ],
                                margin: [0, 0, 0, 4]
                            },
                            {
                                canvas: [{ type: 'line', x1: 0, y1: 0, x2: 736, y2: 0, lineWidth: 0.5, lineColor: '#e5e7eb' }],
                                margin: [0, 0, 0, 10]
                            }
                        );

                        // Style table header row to use brand colour
                        if (doc.styles && doc.styles.tableHeader) {
                            doc.styles.tableHeader.fillColor = brand;
                            doc.styles.tableHeader.color     = '#ffffff';
                            doc.styles.tableHeader.bold      = true;
                        }

                        // Slightly smaller body font for denser tables
                        doc.defaultStyle = doc.defaultStyle || {};
                        doc.defaultStyle.fontSize = 9;

                        // Footer: company name (left) + page number (right)
                        doc.footer = function (currentPage, pageCount) {
                            return {
                                columns: [
                                    { text: company + '  \u2022  Confidential', fontSize: 8, color: '#9ca3af', margin: [40, 6, 0, 0] },
                                    { text: 'Page ' + currentPage + ' of ' + pageCount, alignment: 'right', fontSize: 8, color: '#9ca3af', margin: [0, 6, 40, 0] }
                                ]
                            };
                        };
                    }
                }
            ],

            // Appearance
            info: true,
            autoWidth: false,
            responsive: false,
            stateSave: true,

            // Language customization
            language: {
                search: '',
                searchPlaceholder: 'Search records...',
                lengthMenu: 'Show _MENU_ entries',
                info: 'Showing _START_ to _END_ of _TOTAL_ entries',
                infoEmpty: 'No entries found',
                infoFiltered: '(filtered from _MAX_ total entries)',
                emptyTable: 'No data available',
                paginate: {
                    first: '<i class="bi bi-chevron-double-left"></i>',
                    previous: '<i class="bi bi-chevron-left"></i>',
                    next: '<i class="bi bi-chevron-right"></i>',
                    last: '<i class="bi bi-chevron-double-right"></i>'
                }
            },

            // initComplete: basic class application at init time.
            // The actual bootstrap-select cleanup happens in the window.load
            // handler above (which fires after Jobick's handleSelectPicker).
            initComplete: function () {
                var wrapper = this.api().table().container();
                var $lengthSelect = $(wrapper).find('.dataTables_length select');
                $lengthSelect.css({ width: 'auto', color: '#374151' });
            }
        });
    });

    // ---- Compact DataTable (sort only, no export, for small tables) ----
    $('.ss-datatable-compact').each(function () {
        var $table = $(this);
        var columnDefs = [];
        $table.find('thead th').each(function (index) {
            var text = $(this).text().trim().toLowerCase();
            if (text === 'actions' || text === 'action' || $(this).hasClass('no-sort')) {
                columnDefs.push({ orderable: false, targets: index });
            }
        });

        $table.DataTable({
            paging: false,
            searching: false,
            ordering: true,
            columnDefs: columnDefs,
            info: false,
            autoWidth: false,
            dom: 't',
            language: {
                emptyTable: 'No data available'
            }
        });
    });
});
