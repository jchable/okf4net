SELECT SUM(amount) AS revenue
FROM sales
WHERE year = @year
