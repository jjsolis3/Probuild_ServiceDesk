import fs from 'fs-extra';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Define the source and destination directories
const nodeModulesPath = path.resolve(__dirname, 'node_modules');
const destinationBasePath = path.resolve(__dirname, 'src/assets/libs');

// Load the package configuration file
const configPath = path.resolve(__dirname, 'package-libs-config.json');
let packagesToCopy = [];

try {
    const configContent = fs.readFileSync(configPath, 'utf-8');
    const config = JSON.parse(configContent);
    packagesToCopy = config.packagesToCopy || [];
} catch (err) {
    console.error('Error reading package-libs-config.json:', err);
}

// Function to copy a package
async function copyPackage(packageName) {
    const sourcePath = (fs.existsSync(path.join(nodeModulesPath, packageName + "\\dist"))) ?
        path.join(nodeModulesPath, packageName + "\\dist")
        : path.join(nodeModulesPath, packageName);
    const destinationPath = path.join(destinationBasePath, packageName);

    try {
        // Remove the destination directory if it exists
        if (fs.existsSync(destinationPath)) {
            await fs.remove(destinationPath);
        }

        // Ensure the destination directory exists
        await fs.ensureDir(path.dirname(destinationPath));

        // Copy the package from node_modules to the destination
        await fs.copy(sourcePath, destinationPath, {
            overwrite: true,
            errorOnExist: false,
            recursive: true,
            force: true
        });

        console.log(`Copied ${packageName} to ${destinationPath}`);
    } catch (err) {
        if (err.code === 'EPERM') {
            console.error(`Permission error copying ${packageName}. Try running with administrator privileges.`);
        } else {
            console.error(`Error copying ${packageName}:`, err);
        }
    }
}

// Copy all specified packages
async function copyAllPackages() {
    // Ensure the base destination directory exists
    await fs.ensureDir(destinationBasePath);

    for (const packageName of packagesToCopy) {
        await copyPackage(packageName);
    }
}

// Execute the copy function
copyAllPackages().then(() => {
    console.log('All packages copied successfully.');
}).catch(err => {
    console.error('Error during package copying:', err);
    process.exit(1); // Exit with error code if something goes wrong
});