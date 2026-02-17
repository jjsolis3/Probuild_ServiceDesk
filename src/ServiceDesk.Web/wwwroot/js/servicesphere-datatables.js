/**
 * ServiceSphere DataTables - Global Initialization
 * Automatically initializes DataTables on tables with class "ss-datatable"
 * Provides: Pagination, Search, Column Sorting, PDF/Excel Export
 */
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
                     '<"col-sm-12 col-md-4"l>' +
                     '<"col-sm-12 col-md-4 text-center"B>' +
                     '<"col-sm-12 col-md-4"f>' +
                 '>' +
                 'rtip',
            buttons: [
                {
                    extend: 'excelHtml5',
                    text: '<i class="bi bi-file-earmark-spreadsheet"></i> Excel',
                    className: 'btn btn-sm btn-outline-success',
                    title: exportTitle,
                    exportOptions: {
                        columns: ':not(.no-export)'
                    }
                },
                {
                    extend: 'pdfHtml5',
                    text: '<i class="bi bi-file-earmark-pdf"></i> PDF',
                    className: 'btn btn-sm btn-outline-danger',
                    title: exportTitle,
                    orientation: 'landscape',
                    pageSize: 'LETTER',
                    exportOptions: {
                        columns: ':not(.no-export)'
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
