## General requirements for the C# project

1\. Code quality

- Follow SOLID, DRY, KISS, and YAGNI principles.
- Write readable, clear, and maintainable code.
- Avoid duplication, unnecessary complexity, and unused code.
- Use clear names for classes, methods, and variables.

2\. Architecture

- Maintain separation of concerns between components.
- Ensure loose coupling and high cohesion within modules.
- Use Dependency Injection to manage dependencies.
- Do not introduce unnecessary abstractions or additional architectural layers.

3\. Development standards

- Follow Microsoft C# Coding Conventions.
- Use a consistent formatting style through `.editorconfig`.
- Enable Nullable Reference Types.
- Use a current, supported version of .NET.
- Use modern C# features where appropriate.

4\. Error handling

- Handle exceptions correctly.
- Do not suppress exceptions without justification.
- Use structured logging.
- Do not expose sensitive information in logs.

5\. Performance

- Avoid unnecessary memory allocations and computation.
- Use `async/await` for asynchronous I/O operations.
- Do not block asynchronous code.
- Optimize the use of memory, collections, and external resources.

6\. Security

- Check and validate input data.
- Do not store passwords, tokens, or secrets in source code.
- Follow the principle of least privilege.
- Monitor dependencies for known vulnerabilities.

7\. Testing

- Cover critical business logic with automated tests.
- Ensure tests are independent and reproducible.
- Use unit and integration tests where necessary.
- Do not break existing functionality.

8\. Maintainability

- Split complex logic into small, clear components.
- Avoid tight dependencies and magic values.
- Document complex algorithms and nontrivial decisions.
- Maintain a consistent project structure.

9\. Configuration

- Keep settings outside source code.
- Use standard .NET configuration mechanisms.
- Separate settings for different environments.
- Validate critical configuration parameters.

10\. Build and quality assurance

- The project must build successfully.
- Do not introduce new compiler or analyzer warnings.
- Use static code analysis.
- Provide automated builds and test execution through CI/CD.

### Acceptance criteria

- The code follows the agreed development standards.
- The project builds and runs successfully.
- Automated tests pass.
- There are no critical errors or known vulnerabilities.
- The architecture allows the project to be extended and maintained without unnecessary complexity.

The main requirement: the project must be reliable, secure, understandable to other developers, and easy to develop further.
At the end of the work, always update the information in the documentation: core.md, host.md, and README.md.
