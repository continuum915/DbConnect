-- Tests that need a second database (same-named tables in two databases) create and drop
-- their own rain_scratch_* databases; init scripts run as root, so the grant is given here.
GRANT ALL PRIVILEGES ON `rain\_scratch\_%`.* TO 'rain'@'%';
