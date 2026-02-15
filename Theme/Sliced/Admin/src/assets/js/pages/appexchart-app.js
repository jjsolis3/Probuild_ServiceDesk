// Chart Widget 1
var options = {
    series: [
        {
            data: [10, 82, 40, 65, 20, 89, 40, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#50cd89"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: (formatter = () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart"), options);
chart.render();

// Chart Widget 2
var options = {
    series: [
        {
            data: [10, 82, 40, 65, 20, 89, 40, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#f1416c"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: (formatter = () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart2"), options);
chart.render();

// Chart Widget 3
var options = {
    series: [
        {
            data: [0, 82, 60, 65, 0, 10, 80, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#50cd89"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: (formatter = () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart3"), options);
chart.render();

// Chart Widget 4
var options = {
    series: [
        {
            data: [10, 40, 40, 10, 20, 89, 70, 20, 70, 98],
        },
    ],
    chart: {
        height: 60,
        type: "line",
        fontFamily: "Nunito, sans-serif",
        sparkline: {
            enabled: true,
        },
        dropShadow: {
            enabled: false,
        },
    },
    stroke: {
        curve: "smooth",
        width: 2,
    },
    colors: ["#f1416c"],
    grid: {
        padding: {
            top: 5,
            bottom: 5,
            left: 5,
            right: 5,
        },
    },
    tooltip: {
        x: {
            show: false,
        },
        y: {
            title: {
                formatter: (formatter = () => {
                    return "";
                }),
            },
        },
    },
};

var chart = new ApexCharts(document.querySelector("#chart4"), options);
chart.render();

// Chart Widget 5
var userchart = {
    chart: {
        height: 300,
        type: "area",
        fontFamily: "Inter, sans-serif",
        zoom: {
            enabled: false,
        },
        toolbar: {
            show: false,
        },
    },
    series: [
        {
            name: "Current Week",
            data: [0, 1000, 5000, 10000, 8000, 11000, 15000],
        },
        {
            name: "Previous Week",
            data: [2000, 3000, 6000, 12000, 9000, 13000, 14000],
        },
    ],
    dataLabels: {
        enabled: false,
    },
    stroke: {
        show: true,
        curve: "smooth",
        width: 3,
        lineCap: "square",
    },
    dropShadow: {
        enabled: false,
    },
    colors: ["#6a69f5", "#323a46"],
    markers: {
        discrete: [
            {
                seriesIndex: 0,
                dataPointIndex: 4,
                fillColor: "#6a69f5",
                strokeColor: "#fff",
                size: 6,
            },
            {
                seriesIndex: 1,
                dataPointIndex: 5,
                fillColor: "#323a46",
                strokeColor: "#fff",
                size: 6,
            },
        ],
    },
    labels: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"],
    xaxis: {
        axisBorder: {
            show: false,
        },
        axisTicks: {
            show: false,
        },
        crosshairs: {
            show: true,
        },
        labels: {
            offsetX: 0,
            offsetY: 5,
            style: {
                fontSize: "12px",
                cssClass: "apexcharts-xaxis-title",
            },
        },
    },
    yaxis: {
        tickAmount: 7,
        labels: {
            formatter: (value) => {
                return value / 1000 + "M";
            },
            offsetX: -10,
            offsetY: 0,
            style: {
                fontSize: "12px",
                cssClass: "apexcharts-yaxis-title",
            },
        },
        opposite: false,
    },
    grid: {
        borderColor: "#e0e6ed",
        strokeDashArray: 7,
        xaxis: {
            lines: {
                show: false,
            },
        },
        yaxis: {
            lines: {
                show: true,
            },
        },
        padding: {
            top: 0,
            right: 0,
            bottom: 0,
            left: 0,
        },
    },
    legend: {
        show: false,
    },
    tooltip: {
        marker: {
            show: true,
        },
        x: {
            show: false,
        },
    },
    fill: {
        type: "gradient",
        gradient: {
            shadeIntensity: 1,
            inverseColors: !1,
            opacityFrom: 0,
            opacityTo: 0,
            stops: [100, 100],
        },
    },
};
var chart = new ApexCharts(document.querySelector("#chart5"), userchart);
chart.render();

var options = {
    series: [44, 55, 67],
    chart: {
        height: 320,
        type: "radialBar",
    },
    colors: ["#6a69f5", "#50cd89", "#323a46"],
    plotOptions: {
        radialBar: {
            dataLabels: {
                name: {
                    fontSize: "22px",
                },
                value: {
                    fontSize: "16px",
                },
                total: {
                    show: true,
                    label: "Total",
                    formatter: function (w) {
                        return 709;
                    },
                },
            },
        },
    },
    labels: ["Desktop", "Mobile", "Tablet"],
};

var chart = new ApexCharts(document.querySelector("#chart6"), options);
chart.render();
