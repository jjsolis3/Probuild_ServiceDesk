/*
-------------------------------------------------------------------------
* Template Name    : Sliced - Tailwind CSS Admin & Dashboard Template   * 
* Author           : SRBThemes                                          *
* Version          : 3.0.0                                              *
* Created          : May 2023                                           *
* File Description : Main Js file of the template                       *
*------------------------------------------------------------------------
*/
// linechart
var linechart = {
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
            data: [50, 100, 200, 170, 250, 275, 280],
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
        enabled: true,
        opacity: 0.8,
        blur: 10,
        left: -7,
        top: 22,
    },
    colors: ["#6a69f5"],
    markers: {
        discrete: [
            {
                seriesIndex: 0,
                dataPointIndex: 4,
                fillColor: "#323a46",
                strokeColor: "#fff",
                size: 6,
            },
            {
                seriesIndex: 1,
                dataPointIndex: 5,
                fillColor: "#A8C5DA",
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
        tickAmount: 5,
        labels: {
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
        borderColor: "#e8e8e8",
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
var chart1 = new ApexCharts(document.querySelector("#linechart"), linechart);
chart1.render();

// areachart
var areachart = {
    series: [
        {
            name: "Income",
            data: [
                1680, 1680, 1550, 1780, 1550, 1700, 1900, 1600, 1500, 1700,
                1400, 1700,
            ],
        },
    ],
    chart: {
        type: "area",
        height: 300,
        zoom: {
            enabled: false,
        },
        toolbar: {
            show: false,
        },
    },
    colors: ["#94989a"],
    dataLabels: {
        enabled: false,
    },
    stroke: {
        width: 2,
        curve: "smooth",
    },
    xaxis: {
        axisBorder: {
            color: "#e0e6ed",
        },
    },
    yaxis: {
        opposite: false,
        labels: {
            offsetX: 0,
        },
    },
    labels: [
        "Jan",
        "Feb",
        "Mar",
        "Apr",
        "May",
        "Jun",
        "Jul",
        "Aug",
        "Sep",
        "Oct",
        "Nov",
        "Dec",
    ],
    legend: {
        horizontalAlign: "left",
    },
    grid: {
        borderColor: "#e8e8e8",
    },
};
var chart = new ApexCharts(document.querySelector("#areachart"), areachart);
chart.render();

// barchart
var barchart = {
    series: [
        {
            name: "PRODUCT A",
            data: [44, 55, 41, 67, 22, 43],
        },
        {
            name: "PRODUCT B",
            data: [13, 23, 20, 8, 13, 27],
        },
        {
            name: "PRODUCT C",
            data: [11, 17, 15, 15, 21, 14],
        },
    ],
    chart: {
        type: "bar",
        height: 300,
        stacked: true,
        toolbar: {
            show: false,
        },
        zoom: {
            enabled: false,
        },
    },
    dataLabels: {
        enabled: false,
    },
    colors: ["#323a46", "#50cd89", "#6a69f5"],

    plotOptions: {
        bar: {
            horizontal: false,
            borderRadius: 10,
            columnWidth: "25%",
        },
    },
    xaxis: {
        categories: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
    },
    legend: {
        show: false,
    },
    fill: {
        opacity: 1,
    },
};

var chart = new ApexCharts(document.querySelector("#barchart"), barchart);
chart.render();

// simplebarchart
var simplebarchart = {
    series: [
        {
            name: "Sales",
            data: [2500, 1500, 2000, 3000, 4000, 4500],
        },
    ],
    chart: {
        height: 300,
        type: "bar",
        zoom: {
            enabled: false,
        },
        toolbar: {
            show: false,
        },
    },
    dataLabels: {
        enabled: false,
    },
    stroke: {
        show: true,
        width: 1,
    },
    colors: ["#6a69f5"],
    xaxis: {
        categories: ["Q1", "Q2", "Q3", "Q4", "Q5"],
        axisBorder: {
            color: "#e0e6ed",
        },
    },
    yaxis: {
        opposite: false,
        reversed: false,
    },
    grid: {
        borderColor: "#e8e8e8",
    },
    plotOptions: {
        bar: {
            horizontal: true,
        },
    },
    fill: {
        opacity: 0.8,
    },
};
var chart = new ApexCharts(
    document.querySelector("#simplebarchart"),
    simplebarchart
);
chart.render();

// basicbonut
var basicbonut = {
    series: [38.6, 22.5, 30.8, 8.1],
    chart: {
        height: 300,
        type: "donut",
        zoom: {
            enabled: false,
        },
        toolbar: {
            show: false,
        },
    },
    stroke: {
        show: false,
    },
    labels: ["United States", "Canada", "Mexico", "Other"],
    colors: ["#6a69f5", "#323a46", "#50cd89", "#94989a"],
    responsive: [
        {
            breakpoint: 480,
            options: {
                chart: {
                    width: 200,
                },
            },
        },
    ],
    legend: {
        position: "bottom",
    },
};
var chart = new ApexCharts(document.querySelector("#basicbonut"), basicbonut);
chart.render();

// Barcenter Chart
var barchartcenter = {
    series: [
        {
            name: "Direct",
            data: [12.45, 16.2, 8.9, 11.42, 12.6, 17.1, 17.2, 14.16, 11.1],
        },
        {
            name: "Indirect",
            data: [
                -11.45, -15.42, -7.9, -12.42, -12.6, -17.1, -17.2, -14.16,
                -11.1,
            ],
        },
    ],
    chart: {
        type: "bar",
        height: 350,
        stacked: true,
        toolbar: {
            show: false,
        },
    },
    plotOptions: {
        bar: {
            columnWidth: "20%",
        },
    },
    colors: ["#323a46", "#6a69f5"],
    fill: {
        opacity: 1,
    },
    dataLabels: {
        enabled: false,
    },
    legend: {
        show: false,
    },
    yaxis: {
        labels: {
            formatter: function (y) {
                return y.toFixed(0) + "%";
            },
        },
    },
    xaxis: {
        categories: [
            "Jan",
            "Feb",
            "Mar",
            "Apr",
            "May",
            "Jun",
            "Jul",
            "Aug",
            "Sep",
        ],
        labels: {
            rotate: -90,
        },
    },
};

var chart = new ApexCharts(
    document.querySelector("#barchartcenter"),
    barchartcenter
);
chart.render();

// Doughnut chart
var options2 = {
    chart: {
        height: 280,
        type: "radialBar",
    },
    series: [67],
    colors: ["#6a69f5"],
    plotOptions: {
        radialBar: {
            startAngle: -90,
            endAngle: 90,
            track: {
                background: "#323a46",
                startAngle: -90,
                endAngle: 90,
            },
            dataLabels: {
                name: {
                    show: false,
                },
                value: {
                    fontSize: "20px",
                    show: true,
                },
            },
        },
    },
    fill: {
        type: "gradient",
        gradient: {
            shade: "light",
            type: "horizontal",
            gradientToColors: ["#6a69f5"],
            stops: [0, 100],
        },
    },
    stroke: {
        lineCap: "butt",
    },
    labels: ["Progress"],
};

new ApexCharts(document.querySelector("#doughnutchart"), options2).render();

var options = {
    chart: {
        height: 350,
        type: "area",
        stacked: true,
        toolbar: {
            show: false,
            autoSelected: "zoom",
        },
    },
    colors: ["#6a69f5", "#323a46"],
    dataLabels: {
        enabled: false,
    },
    stroke: {
        curve: "smooth",
        width: [3, 3],
        dashArray: [0, 4],
        lineCap: "round",
    },
    grid: {
        borderColor: "#e8e8e8",
        padding: {
            left: 0,
            right: 0,
        },
        strokeDashArray: 4,
    },
    markers: {
        size: 0,
        hover: {
            size: 0,
        },
    },
    series: [
        {
            name: "New Visits",
            data: [0, 60, 20, 90, 45, 110, 55, 130, 44, 110, 75, 120],
        },
        {
            name: "Unique Visits",
            data: [0, 45, 10, 75, 35, 94, 40, 115, 30, 105, 65, 110],
        },
    ],

    xaxis: {
        type: "month",
        categories: [
            "Jan",
            "Feb",
            "Mar",
            "Apr",
            "May",
            "Jun",
            "Jul",
            "Aug",
            "Sep",
            "Oct",
            "Nov",
            "Dec",
        ],
        axisBorder: {
            show: true,
            color: "#e8e8e8",
        },
        axisTicks: {
            show: true,
            color: "#e8e8e8",
        },
    },
    fill: {
        type: "gradient",
        gradient: {
            shadeIntensity: 1,
            opacityFrom: 0.4,
            opacityTo: 0.3,
            stops: [0, 90, 100],
        },
    },

    tooltip: {
        x: {
            format: "dd/MM/yy HH:mm",
        },
    },
    legend: {
        position: "top",
        horizontalAlign: "right",
    },
};

var chart = new ApexCharts(document.querySelector("#apex_area1"), options);

chart.render();
